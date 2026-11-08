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
            AutoTechnologySettings.ClearEmpty(instance); // marks persist on disk between runs

            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance));
            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance), "Growth stays first while it has research");
            AutoTechnologySettings.MarkEmpty(instance, ResearchType.Growth);
            Assert.AreEqual(ResearchType.Economy, settings.NextResearchType(instance));
            AutoTechnologySettings.MarkEmpty(instance, ResearchType.Economy);
            Assert.AreEqual(ResearchType.Battle, settings.NextResearchType(instance));
        }

        [Test]
        public void TreesWaitingOnTheAcademyStaySkippedUntilItIsUpgraded()
        {
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchTypes\":[0,1,2]}")!;
            const int instance = 9003;
            AutoTechnologySettings.ClearEmpty(instance);
            foreach (var t in new[] { ResearchType.Growth, ResearchType.Economy, ResearchType.Battle })
                AutoTechnologySettings.MarkEmpty(instance, t, System.TimeSpan.FromHours(6));
            Assert.That(settings.AllEmpty(instance), Is.True, "nothing to scan while the Academy blocks every tree");
            AutoTechnologySettings.ClearEmpty(instance);
            Assert.That(settings.AllEmpty(instance), Is.False);
            Assert.AreEqual(ResearchType.Growth, settings.NextResearchType(instance));
        }

        [Test]
        public void NextResearchType_WithoutList_UsesSingleResearchType()
        {
            var settings = JsonSerializer.Deserialize<AutoTechnologySettings>("{\"ResearchType\":2}")!;
            Assert.AreEqual(ResearchType.Battle, settings.NextResearchType(9002));
        }
    }
}
