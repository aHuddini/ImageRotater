using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // A plugin XAML file may not name a packaged third-party assembly in an
    // xmlns, and this is not a style rule - it is a hard limit of how Playnite
    // loads us.
    //
    // Playnite loads extension assemblies with Assembly.LoadFrom, whose probing
    // covers the file's own directory. That is why calling XamlAnimatedGif from
    // code-behind works: the reference resolves in the LoadFrom context, which
    // knows where we came from.
    //
    // BAML does not use that context. Resolving a xmlns goes through
    // Baml2006SchemaContext.ResolveAssembly -> Assembly.Load(AssemblyName),
    // which is the default Load context and probes only the AppDomain base
    // directory - Playnite's own folder - and never the extension folder. The
    // DLL sits right beside ours and is still "could not load file or assembly".
    //
    // It fails at InitializeComponent, before a single element is built, so the
    // whole view dies rather than just losing the feature. That was the search
    // dialog refusing to open at all:
    //
    //   Could not load file or assembly 'XamlAnimatedGif,
    //   PublicKeyToken=20a987d8023d9690' or one of its dependencies.
    //
    // Worse, a DataTrigger does not protect you - BAML resolves the setter's
    // type converter while parsing, so merely declaring the setter is enough to
    // kill the view on every open, animated result or not.
    //
    // Reaching for such a type from code-behind is always allowed. Only the
    // xmlns is banned.
    [TestFixture]
    public class XamlAssemblyProbingTests
    {
        // Namespaces WPF itself provides, resolved from assemblies already
        // loaded in the host: framework schemas, and clr-namespace declarations
        // pointing at Playnite's SDK or at our own assembly.
        private static readonly Regex SafeNamespace = new Regex(
            @"^(http://schemas\.microsoft\.com/|http://schemas\.openxmlformats\.org/"
            + @"|clr-namespace:(ImageRotater|Playnite\.SDK|System)\b)",
            RegexOptions.IgnoreCase);

        private static string ControlsDirectory()
        {
            // Walk up from the test binaries to the repository root, so the test
            // does not care whether it runs from bin\Debug or bin\Release.
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Controls")))
            {
                dir = dir.Parent;
            }

            Assert.IsNotNull(dir, "could not locate src\\Controls from the test directory");
            return Path.Combine(dir.FullName, "src", "Controls");
        }

        [Test]
        public void NoXamlDeclaresAThirdPartyAssemblyNamespace()
        {
            string controls = ControlsDirectory();

            foreach (string file in Directory.GetFiles(controls, "*.xaml", SearchOption.AllDirectories))
            {
                string xaml = File.ReadAllText(file);

                foreach (Match m in Regex.Matches(xaml, @"xmlns(?::\w+)?\s*=\s*""([^""]+)"""))
                {
                    string ns = m.Groups[1].Value;

                    Assert.IsTrue(SafeNamespace.IsMatch(ns),
                        $"{Path.GetFileName(file)} declares xmlns \"{ns}\". BAML resolves this "
                        + "with Assembly.Load, which never probes the extension folder, so the "
                        + "view throws XamlParseException at InitializeComponent and never opens. "
                        + "Use the type from code-behind instead.");
                }
            }
        }
    }
}
