using Bot.Core.Tasks.Modules;
using NUnit.Framework;
using System.Linq;

namespace Bot.Core.Tests
{
    public class ResearchDialogTests
    {
        [Test]
        public void FindsResearchOnAShortBattleDialog()
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "research-battle-dialog.png"));
            var button = AutoTechnologyTask.FindResearchButton(shot);
            Assert.That(button, Is.Not.Null);
            Assert.That(button!.Value.X, Is.InRange(400, 620));
            Assert.That(button.Value.Y, Is.InRange(950, 1010));
        }

        [TestCase("research-battle-tree.png", 4)]   // Weapons Prep I + the three items it opened
        [TestCase("research-economy-tree.png", 4)]  // Iron Mining II + Bread/Stone/Wood Output
        public void FindsTheOpenItemsOfATree(string file, int open)
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, file));
            var items = AutoTechnologyTask.FindTreeItems(shot);
            TestContext.Progress.WriteLine(file + ": " + string.Join(" ", items.Select(p => $"({p.X},{p.Y}){(AutoTechnologyTask.IsUnlocked(shot, p) ? "+" : "-")}")));
            Assert.That(items.Count(p => AutoTechnologyTask.IsUnlocked(shot, p)), Is.EqualTo(open));
        }
    }
}
