using System.Collections.Generic;
using System.Text;
using Xunit;

namespace MBBSEmu.Tests.ExportedModules.Majorbbs
{
    public class byenow_Tests : ExportedModuleTestBase
    {
        private const int BYENOW_ORDINAL = 91;
        private const ushort NOMSG = 0;
        private const ushort PAMSG = 0xFFFF; // -1

        [Fact]
        public void byenow_NOMSG_MarksSessionForHangup_Test()
        {
            Reset();
            mbbsEmuMemoryCore.SetWord("USRNUM", 1);

            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, BYENOW_ORDINAL, new List<ushort> { NOMSG });

            Assert.NotNull(testSessions[1].ByeByeDeadline);
            Assert.Null(testSessions[0].ByeByeDeadline);
            Assert.Empty(testSessions[1].DataToClient);
        }

        [Fact]
        public void byenow_PAMSG_SendsAlreadyPrfdText_Test()
        {
            Reset();
            mbbsEmuMemoryCore.SetWord("USRNUM", 0);
            testSessions[0].OutputEnabled = false; //unit-test sessions can't render text variables
            SetPrf("Bye!");

            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, BYENOW_ORDINAL, new List<ushort> { PAMSG });

            Assert.NotNull(testSessions[0].ByeByeDeadline);
            //prfbuf was flushed to the channel (and cleared)
            Assert.Equal(mbbsEmuMemoryCore.GetVariablePointer("PRFBUF"), mbbsEmuMemoryCore.GetPointer("PRFPTR"));
        }

        [Fact]
        public void byenow_VacantChannel_DoesNothing_Test()
        {
            Reset();
            mbbsEmuMemoryCore.SetWord("USRNUM", 3); // no session on channel 3

            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, BYENOW_ORDINAL, new List<ushort> { NOMSG });

            Assert.Null(testSessions[0].ByeByeDeadline);
            Assert.Null(testSessions[1].ByeByeDeadline);
        }

        private void SetPrf(string value)
        {
            mbbsEmuMemoryCore.SetArray("PRFBUF", Encoding.ASCII.GetBytes(value));
            var prfPointer = mbbsEmuMemoryCore.GetPointer("PRFPTR");
            prfPointer.Offset += (ushort)value.Length;
            mbbsEmuMemoryCore.SetPointer("PRFPTR", prfPointer);
        }
    }
}
