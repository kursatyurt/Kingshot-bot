using System.Text.Json;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class AutoTechnologySettingsTests
    {
        [Test]
        public void NextResearchType_FollowsPriorityAndSkipsEmptyTrees()
        {
            // As stored in config: taskSettings["AutoTechnology"] = {"ResearchTypes":[0,1,2]} (Growth, Economy, Battle)
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchTypes\":[0,1,2]}")!;
            const int instance = 9001;

            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance));
            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance), "Growth stays first while it has research");
            AutoTechnologySettings.MarkEmpty(instance, ResearchType.Growth);
            Assert.AreEqual(ResearchType.Economy, settings.NextResearchType(instance));
            AutoTechnologySettings.MarkEmpty(instance, ResearchType.Economy);
            Assert.AreEqual(ResearchType.Battle, settings.NextResearchType(instance));
        }

        [Test]
        public void NextResearchType_WithoutList_UsesSingleResearchType()
        {
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchType\":2}")!;
            Assert.AreEqual(ResearchType.Battle, settings.NextResearchType(9002));
        }
    }
}
