using System.Collections.Generic;
using Xunit;

namespace MBBSEmu.Tests.ExportedModules.Majorbbs
{
    public class curusr_Tests : ExportedModuleTestBase
    {
        private const int CURUSR_ORDINAL = 151;

        [Fact]
        public void CURUSR_Test()
        {
            //Reset State
            Reset();

            Assert.Equal(0, mbbsEmuMemoryCore.GetWord("USRNUM"));

            //Execute Test
            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, CURUSR_ORDINAL, new List<ushort> { 1 });

            Assert.Equal(1, mbbsEmuMemoryCore.GetWord("USRNUM"));
        }

        [Fact]
        public void CURUSR_vacant_channel_points_at_that_channel_Test()
        {
            //Reset State
            Reset();

            //Channel 3 is within NTERMS but has no connected session (test sessions are 0 and 1)
            ushort vacant = 3;
            Assert.False(testSessions.ContainsKey(vacant));
            Assert.True(vacant < mbbsEmuMemoryCore.GetWord("NTERMS"));

            //Execute Test
            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, CURUSR_ORDINAL, new List<ushort> { vacant });

            //Like MAJORBBS.C: usrnum/usrptr move to the vacant channel, never stay on the previous user
            Assert.Equal(vacant, mbbsEmuMemoryCore.GetWord("USRNUM"));
            var userBase = mbbsEmuMemoryCore.GetVariablePointer("USER");
            var usrptr = mbbsEmuMemoryCore.GetPointer("*USRPTR");
            Assert.Equal(userBase.Segment, usrptr.Segment);
            Assert.Equal((ushort)(userBase.Offset + MBBSEmu.HostProcess.Structs.User.Size * vacant), usrptr.Offset);
        }

        [Fact]
        public void CURUSR_invalid_Test()
        {
            //Reset State
            Reset();

            Assert.Equal(0, mbbsEmuMemoryCore.GetWord("USRNUM"));

            //Execute Test
            ExecuteApiTest(HostProcess.ExportedModules.Majorbbs.Segment, CURUSR_ORDINAL, new List<ushort> { 1000 });

            Assert.Equal(0, mbbsEmuMemoryCore.GetWord("USRNUM"));
        }
    }
}
