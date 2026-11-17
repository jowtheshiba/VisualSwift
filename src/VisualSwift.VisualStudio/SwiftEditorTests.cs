using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal static class SwiftEditorTests
    {
        internal static async Task<string> FindAsync(Func<string, object, Task<JToken>> request, string uri, string workspace, int line, int column, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var tests = await request("textDocument/tests", new { textDocument = new { uri } }).WithCancellation(timeout.Token).ConfigureAwait(false);
                var file = new Uri(uri);
                var test = FindAt(tests, file, line, column);
                if (test == null) throw new InvalidOperationException("Place the cursor in an XCTest test method or a Swift Testing @Test function.");

                var description = await Task.Run(() => SwiftProcess.RunAsync(Toolchain.Resolve(workspace),
                    "package describe --type json", workspace, token), token).ConfigureAwait(false);
                var name = ResolveName(test, JObject.Parse(description), workspace, file.LocalPath);
                if (name == null) throw new InvalidOperationException("The test could not be matched to a SwiftPM test target.");
                return name;
            }
        }

        internal static JToken FindAt(JToken tests, Uri uri, int line, int column)
        {
            if (!(tests is JArray items)) return null;
            foreach (var item in items)
            {
                if (item["children"] is JArray children && children.Count > 0)
                {
                    var match = FindAt(children, uri, line, column);
                    if (match != null) return match;
                    continue;
                }

                var id = (string)item["id"];
                var location = item["location"];
                if (id?.EndsWith(")", StringComparison.Ordinal) != true ||
                    !Uri.TryCreate((string)location?["uri"], UriKind.Absolute, out var file) ||
                    !file.IsFile || !string.Equals(file.LocalPath, uri.LocalPath, StringComparison.OrdinalIgnoreCase)) continue;

                var start = location["range"]?["start"];
                var end = location["range"]?["end"];
                var startLine = (int?)start?["line"];
                var endLine = (int?)end?["line"];
                if (line >= startLine && line <= endLine &&
                    (line != startLine || column >= (int?)start?["character"]) &&
                    (line != endLine || column < (int?)end?["character"])) return item;
            }
            return null;
        }

        internal static string ResolveName(JToken test, JObject description, string workspace, string file)
        {
            var modules = (description["targets"] as JArray ?? new JArray())
                .Where(target => (string)target["type"] == "test" && (string)target["path"] != null &&
                    target["sources"] is JArray sources && sources.Values<string>().Any(source =>
                        string.Equals(Path.GetFullPath(Path.Combine(workspace, (string)target["path"], source)), file, StringComparison.OrdinalIgnoreCase)))
                .Select(target => (string)target["c99name"]).Where(module => !string.IsNullOrEmpty(module)).Distinct().ToArray();
            if (modules.Length != 1) return null;

            var prefix = modules[0] + ".";
            var id = (string)test["id"];
            if (id.StartsWith(prefix, StringComparison.Ordinal)) id = id.Substring(prefix.Length);
            switch ((string)test["style"])
            {
                case "XCTest":
                    var separator = id.LastIndexOf('/');
                    if (separator < 0 || !id.EndsWith("()", StringComparison.Ordinal)) return null;
                    return prefix + id.Substring(0, separator).Replace('/', '.') + id.Substring(separator, id.Length - separator - 2);
                case "swift-testing":
                    return prefix + id;
                default:
                    return null;
            }
        }

        internal static async Task RunAsync(IComponentModel components, string manifest, string testName, CancellationToken token)
        {
            // Test Explorer's selected-test API is internal. Its exact query keeps
            // Swift Testing argument labels out of the legacy VSTest filter syntax.
            var interfaces = typeof(ITestContainer).Assembly;
            var assemblyName = new AssemblyName(interfaces.FullName) { Name = "Microsoft.VisualStudio.TestWindow" };
            var testWindow = Assembly.Load(assemblyName);
            var service = GetExport(components, testWindow.GetType("Microsoft.VisualStudio.TestWindow.Controller.ICoreVsTestService"));
            var contract = interfaces.GetType("Microsoft.VisualStudio.TestWindow.Extensibility.IVsTestService");
            var run = contract?.GetMethod("RunTestsAsync");
            var getTests = contract?.GetMethod("GetTestsAsync");
            if (run == null || getTests == null) throw TestExplorerUnavailable();

            var queryType = run.GetParameters()[0].ParameterType;
            var addFilter = queryType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic);
            if (addFilter == null) throw TestExplorerUnavailable();
            var filterParameters = addFilter.GetParameters();
            var property = filterParameters[0].ParameterType;
            var exact = Enum.Parse(filterParameters[2].ParameterType, "ExactMatch");
            var query = Activator.CreateInstance(queryType, true);
            addFilter.Invoke(query, new[] { Enum.Parse(property, "Source"), new[] { manifest }, exact });
            addFilter.Invoke(query, new[] { Enum.Parse(property, "FullyQualifiedName"), new[] { testName }, exact });
            var args = new[] { query, (object)token };

            if (!await HasTestAsync(service, getTests, args))
            {
                var container = components.DefaultExportProvider.GetExportedValues<ITestContainerDiscoverer>().OfType<SwiftTestContainers>()
                    .SelectMany(discoverer => discoverer.TestContainers)
                    .FirstOrDefault(candidate => string.Equals(candidate.Source, manifest, StringComparison.OrdinalIgnoreCase));
                if (container == null) throw new InvalidOperationException("Test Explorer has no SwiftPM test container for this package.");
                var factoryType = testWindow.GetType("Microsoft.VisualStudio.TestWindow.Controller.IRequestFactory");
                var discover = factoryType?.GetMethod("ProcessChangedContainersAsync");
                if (discover == null) throw TestExplorerUnavailable();
                var factory = GetExport(components, factoryType);
                await ((Task)discover.Invoke(factory, new object[] { new[] { container }, false })).WithCancellation(token);
                if (!await HasTestAsync(service, getTests, args))
                    throw new InvalidOperationException("Test Explorer could not discover " + testName + ". Check the Tests output pane.");
            }

            // Test Explorer reports the outcome itself. Its return value can be
            // false even when the selected test ran and passed.
            await ((Task)run.Invoke(service, args)).ConfigureAwait(false);
        }

        private static async Task<bool> HasTestAsync(object service, MethodInfo getTests, object[] args)
        {
            var request = (Task)getTests.Invoke(service, args);
            await request.ConfigureAwait(false);
            return ((IEnumerable)request.GetType().GetProperty("Result").GetValue(request)).Cast<object>().Any();
        }

        private static object GetExport(IComponentModel components, Type contract)
        {
            if (contract == null) throw TestExplorerUnavailable();
            return components.DefaultExportProvider.GetExports(contract, null, null).SingleOrDefault()?.Value
                ?? throw TestExplorerUnavailable();
        }

        private static InvalidOperationException TestExplorerUnavailable() =>
            new InvalidOperationException("Visual Studio Test Explorer is unavailable.");
    }
}
