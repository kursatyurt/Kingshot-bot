using System.Reflection;
using System.Runtime.CompilerServices;
using Bot.Core.Tasks;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class TemplateFolderTests
    {
        // Every task looks up its images in templates/images/<GetImageFolderName()>. A wrong name only
        // shows up at runtime as "Template not found", and Windows' case-insensitive paths can hide it.
        [Test]
        public void EveryTaskImageFolderExists()
        {
            var templates = Path.Combine(AppContext.BaseDirectory, "templates", "images");
            var folders = Directory.GetDirectories(templates).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            var getFolder = typeof(BaseTaskWithCommonPatterns).GetMethod("GetImageFolderName", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var tasks = typeof(BaseTaskWithCommonPatterns).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(BaseTaskWithCommonPatterns)) && !t.IsAbstract)
                .Where(t => t != typeof(Bot.Core.Tasks.Modules.AccountDetectionTask)); // OCR only, no templates
            var missing = tasks
                // Constructors need services; the folder name and TaskType are constants, so skip them
                .Select(t => (Type: t, Folder: (string)getFolder.Invoke(RuntimeHelpers.GetUninitializedObject(t), null)!))
                .Where(x => !folders.Contains(x.Folder))
                .Select(x => $"{x.Type.Name} -> {x.Folder}")
                .ToList();

            Assert.IsEmpty(missing, "Tasks whose image folder does not exist: " + string.Join(", ", missing));
        }
    }
}
