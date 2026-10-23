using MBBSEmu.HostProcess.Structs;
using Xunit;

namespace MBBSEmu.Tests.Integration
{
    /// <summary>
    ///     A disconnected channel must not keep a USER[] record that says it is still inside a module,
    ///     otherwise modules that sweep every channel (MajorMUD) keep treating it as a live player.
    /// </summary>
    [Collection("Non-Parallel")]
    public class RemoveSessionClearsUser_Tests : MBBSEmuIntegrationTestBase
    {
        [Fact]
        public void RemoveSession_ZeroesUserRecord()
        {
            ExecuteTest((session, host) =>
            {
                WaitUntil(':', "Make your selection");

                var memory = host.GetModule("MBBSEMU").Memory;
                var userBase = memory.GetVariablePointer("USER");
                var slot = new MBBSEmu.Memory.FarPtr(userBase.Segment, (ushort)(userBase.Offset + User.Size * session.Channel));

                //Inside the module: record says so
                Assert.NotEqual(0, memory.GetWord(slot.Segment, (ushort)(slot.Offset + 6)));

                Assert.True(host.RemoveSession(session.Channel));

                foreach (var b in memory.GetArray(slot, User.Size).ToArray())
                    Assert.Equal(0, b);
            });
        }
    }
}
