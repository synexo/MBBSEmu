using Iced.Intel;
using MBBSEmu.Disassembler.Artifacts;
using MBBSEmu.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Decoder = Iced.Intel.Decoder;

namespace MBBSEmu.Memory
{
    /// <summary>
    ///     Handles Memory Operations for the Module
    ///
    ///     Information of x86 Memory Segmentation: https://en.wikipedia.org/wiki/X86_memory_segmentation
    /// </summary>
    public class ProtectedModeMemoryCore : AbstractMemoryCore, IMemoryCore
    {
        private readonly byte[][] _memorySegments = new byte[0x10000][];
        private readonly Segment[] _segments = new Segment[0x10000];
        private readonly Instruction[][] _decompiledSegments = new Instruction[0x10000][];

        private const ushort HEAP_BASE_SEGMENT = 0x1000;    // heap starts here
        private const ushort HEAP_CEILING_SEGMENT = 0x2FFF; // heap ends here: 2x original (512MB max)
        private FarPtr _nextHeapPointer = new FarPtr(HEAP_BASE_SEGMENT, 0);
        private const ushort REALMODE_BASE_SEGMENT = 0x3000; // must be above HEAP_CEILING_SEGMENT
        private FarPtr _currentRealModePointer = new FarPtr(REALMODE_BASE_SEGMENT, 0);
        private readonly PointerDictionary<Dictionary<ushort, FarPtr>> _bigMemoryBlocks = new();
        private readonly Dictionary<ushort, MemoryAllocator> _heapAllocators = new();

        /// <summary>
        ///     Protects all heap state: _heapAllocators, _nextHeapPointer, and the
        ///     MemoryAllocator instances they contain (whose _freeBlocks / RemainingBytes
        ///     are not individually thread-safe).
        ///
        ///     RTIHDLR/SYSCYC (18 Hz background routines) and user-input processing can
        ///     call Malloc/Free concurrently; without this lock the Dictionary iteration
        ///     in Malloc races with Add in the new-segment path, and the LinkedList free
        ///     lists inside each MemoryAllocator corrupt silently.
        /// </summary>
        private readonly object _heapLock = new();

        /// <summary>
        ///     Tracks the most recently created heap allocator so Malloc can try it first,
        ///     avoiding an O(n) scan across all fragmented segments on every allocation.
        /// </summary>
        private MemoryAllocator _lastAllocator;

        public ProtectedModeMemoryCore(IMessageLogger logger) : base(logger)
        {
            //Add Segment 0 by default, stack segment
            AddSegment(0);
        }

        public override FarPtr Malloc(uint size)
        {
            lock (_heapLock)
            {
                // Fast path: try the most recently created allocator first.
                // The vast majority of allocations succeed here, avoiding the
                // O(n) scan across all older (fragmented) segments.
                if (_lastAllocator != null && _lastAllocator.RemainingBytes >= size)
                {
                    var ptr = _lastAllocator.Malloc(size);
                    if (!ptr.IsNull())
                        return ptr;
                }

                // Slow path: scan all existing allocators.
                foreach (var allocator in _heapAllocators.Values)
                {
                    if (allocator.RemainingBytes < size)
                        continue;

                    var ptr = allocator.Malloc(size);
                    if (!ptr.IsNull())
                        return ptr;
                }

                // All existing segments are full or too fragmented — create a new one.
                if (_nextHeapPointer.Segment >= HEAP_CEILING_SEGMENT)
                {
                    _logger.Error($"Heap exhausted: all segments 0x{HEAP_BASE_SEGMENT:X4}-0x{HEAP_CEILING_SEGMENT:X4} consumed ({(HEAP_CEILING_SEGMENT - HEAP_BASE_SEGMENT) * 64}KB heap limit reached)");
                    return FarPtr.Empty;
                }

                AddSegment(_nextHeapPointer.Segment);

                // I hate null pointers/offsets so start the allocator at offset 2
                var memoryAllocator = new MemoryAllocator(_logger, _nextHeapPointer + 2, 0xFFFE, alignment: 2);
                _heapAllocators.Add(_nextHeapPointer.Segment, memoryAllocator);
                _lastAllocator = memoryAllocator;

                _nextHeapPointer.Segment++;

                var segmentsUsed = _nextHeapPointer.Segment - HEAP_BASE_SEGMENT;
                var totalSegments = HEAP_CEILING_SEGMENT - HEAP_BASE_SEGMENT;
                var percentUsed = segmentsUsed * 100 / totalSegments;
                var prevPercentUsed = (segmentsUsed - 1) * 100 / totalSegments;

                if (percentUsed != prevPercentUsed && percentUsed % 10 == 0)
                    _logger.Warn($"Heap usage at {percentUsed}%: {segmentsUsed} of {totalSegments} segments consumed ({segmentsUsed * 64}KB of {totalSegments * 64}KB)");

                return memoryAllocator.Malloc(size);
            }
        }

        public override void Free(FarPtr ptr)
        {
            if (ptr.IsNull())
                return;

            lock (_heapLock)
            {
                if (!_heapAllocators.TryGetValue(ptr.Segment, out var memoryAllocator))
                {
                    _logger.Error($"Attempted to deallocate memory from an unknown segment {ptr}");
                    return;
                }

                memoryAllocator.Free(ptr);
            }
        }

        public int GetAllocatedMemorySize(FarPtr ptr)
        {
            lock (_heapLock)
            {
                if (!_heapAllocators.TryGetValue(ptr.Segment, out var memoryAllocator))
                    return -1;

                return memoryAllocator.GetAllocatedMemorySize(ptr);
            }
        }

        /// <summary>
        ///     Returns percent of heap segments consumed (for diagnostic use by galmalloc)
        /// </summary>
        public int HeapPercentUsed
        {
            get
            {
                lock (_heapLock)
                {
                    var segmentsUsed = _nextHeapPointer.Segment - HEAP_BASE_SEGMENT;
                    var totalSegments = HEAP_CEILING_SEGMENT - HEAP_BASE_SEGMENT;
                    return segmentsUsed * 100 / totalSegments;
                }
            }
        }

        public string ToHexString(FarPtr ptr, int length)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Deletes all defined Segments from Memory
        /// </summary>
        public override void Clear()
        {
            base.Clear();

            lock (_heapLock)
            {
                Array.Clear(_memorySegments, 0, _memorySegments.Length);
                Array.Clear(_segments, 0, _segments.Length);
                Array.Clear(_decompiledSegments, 0, _decompiledSegments.Length);

                _nextHeapPointer = new FarPtr(HEAP_BASE_SEGMENT, 0);
                _currentRealModePointer = new FarPtr(REALMODE_BASE_SEGMENT, 0);
                _bigMemoryBlocks.Clear();
                _heapAllocators.Clear();
                _lastAllocator = null;
            }
        }

        /// <summary>
        ///     Adds a new Memory Segment containing 65536 bytes
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <param name="size"></param>
        public void AddSegment(ushort segmentNumber, int size = 0x10000)
        {
            if (_memorySegments[segmentNumber] != null)
                throw new Exception($"Segment with number {segmentNumber} already defined");

            _memorySegments[segmentNumber] = new byte[size];
        }

        /// <summary>
        ///     Removes the specified segment. Typically only used by a test.
        /// </summary>
        /// <param name="segment"></param>
        public void RemoveSegment(ushort segment)
        {
            _memorySegments[segment] = null;
            _segments[segment] = null;
            _decompiledSegments[segment] = null;
        }

        /// <summary>
        ///     Directly adds a raw segment from an NE file segment
        /// </summary>
        /// <param name="segment"></param>
        public void AddSegment(Segment segment)
        {
            //Get Address for this Segment
            var segmentMemory = new byte[0x10000];

            //Add the data to memory and record the segment offset in memory
            Array.Copy(segment.Data, 0, segmentMemory, 0, segment.Data.Length);
            _memorySegments[segment.Ordinal] = segmentMemory;

            if (segment.Flags.Contains(EnumSegmentFlags.Code))
            {
                //Decode the Segment
                var instructionList = new InstructionList();
                var codeReader = new ByteArrayCodeReader(segment.Data);
                var decoder = Decoder.Create(16, codeReader);
                decoder.IP = 0x0;

                while (decoder.IP < (ulong)segment.Data.Length)
                {
                    decoder.Decode(out instructionList.AllocUninitializedElement());
                }

                _decompiledSegments[segment.Ordinal] = new Instruction[0x10000];
                foreach (var i in instructionList)
                {
                    _decompiledSegments[segment.Ordinal][i.IP16] = i;
                }
            }

            _segments[segment.Ordinal] = segment;
        }

        /// <summary>
        ///     Adds a Decompiled code segment
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <param name="segmentInstructionList"></param>
        public void AddSegment(ushort segmentNumber, InstructionList segmentInstructionList)
        {
            _decompiledSegments[segmentNumber] = new Instruction[0x10000];
            foreach (var i in segmentInstructionList)
            {
                _decompiledSegments[segmentNumber][i.IP16] = i;
            }
        }

        /// <summary>
        ///     Returns the Segment information for the desired Segment Number
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public Segment GetSegment(ushort segmentNumber) => _segments[segmentNumber];

        /// <summary>
        ///     Verifies the specified segment is defined
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public bool HasSegment(ushort segmentNumber) => _memorySegments[segmentNumber] != null;

        /// <summary>
        ///     Returns the decompiled instruction from the specified segment:pointer
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="instructionPointer"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public Instruction GetInstruction(ushort segment, ushort instructionPointer) => _decompiledSegments[segment][instructionPointer];

        [MethodImpl(CompilerOptimizations)]
        public Instruction Recompile(ushort segment, ushort instructionPointer)
        {
            //If it wasn't able to decompile linear through the data, there might have been
            //data in the path of the code that messed up decoding, in this case, we grab up to
            //6 bytes at the IP and decode the instruction manually. This works 9 times out of 10
            ReadOnlySpan<byte> segmentData = GetArray(segment, instructionPointer, 6);
            var reader = new ByteArrayCodeReader(segmentData.ToArray());
            var decoder = Decoder.Create(16, reader);
            decoder.IP = instructionPointer;
            decoder.Decode(out var outputInstruction);

            _decompiledSegments[segment][instructionPointer] = outputInstruction;
            return outputInstruction;
        }

        [MethodImpl(CompilerOptimizations)]
        public override Span<byte> VirtualToPhysical(ushort segment, ushort offset) =>_memorySegments[segment].AsSpan(offset);

        /// <summary>
        ///     Allocates the specific number of Big Memory Blocks with the desired size
        /// </summary>
        /// <param name="quantity"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        public override FarPtr AllocateBigMemoryBlock(ushort quantity, ushort size)
        {
            var newBlockOffset = _bigMemoryBlocks.Allocate(new Dictionary<ushort, FarPtr>());

            //Fill the Region
            for (ushort i = 0; i < quantity; i++)
                _bigMemoryBlocks[newBlockOffset].Add(i, AllocateVariable($"ALCBLOK-{newBlockOffset}-{i}", size));

            return new FarPtr(0xFFFF, (ushort)newBlockOffset);
        }

        /// <summary>
        ///     Returns the specified block by index in the desired memory block
        /// </summary>
        /// <param name="block"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public override FarPtr GetBigMemoryBlock(FarPtr block, ushort index) => _bigMemoryBlocks[block.Offset][index];

        /// <summary>
        ///     Returns a newly allocated Segment in "Real Mode" memory.
        ///     Skips over any segment numbers already in use (e.g. by the heap allocator)
        ///     to prevent collisions.
        /// </summary>
        /// <returns></returns>
        public override FarPtr AllocateRealModeSegment(ushort segmentSize = ushort.MaxValue)
        {
            do
            {
                _currentRealModePointer.Segment++;
            } while (_memorySegments[_currentRealModePointer.Segment] != null &&
                     _currentRealModePointer.Segment < 0xFFFF);

            var realModeSegment = new FarPtr(_currentRealModePointer);
            AddSegment(realModeSegment.Segment, segmentSize);
            return realModeSegment;
        }

        public ReadOnlySpan<byte> GetMemorySegment(ushort segment) => _memorySegments[segment];
    }
}
