using System.Text.Json;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class AutoTechnologySettingsTests
    {
        [Test]
        public void NextResearchType_AlternatesOnlyAfterAdvance()
        {
            // As stored in config: taskSettings["AutoTechnology"] = {"ResearchTypes":[1,0]} (Economy, Growth)
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchTypes\":[1,0]}")!;
            const int instance = 9001;

            Assert.AreEqual(ResearchType.Economy, settings.NextResearchType(instance));
            Assert.AreEqual(ResearchType.Economy, settings.NextResearchType(instance), "no research started, so no rotation");
            AutoTechnologySettings.AdvanceResearchType(instance);
            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance));
            AutoTechnologySettings.AdvanceResearchType(instance);
            Assert.AreEqual(ResearchType.Economy, settings.NextResearchType(instance));
        }

        [Test]
        public void NextResearchType_WithoutList_UsesSingleResearchType()
        {
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchType\":2}")!;
            AutoTechnologySettings.AdvanceResearchType(9002);
            Assert.AreEqual(ResearchType.Battle, settings.NextResearchType(9002));
        }
    }
}
