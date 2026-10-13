using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Newtonsoft.Json.Linq;

namespace VisualSwift.Testing
{
    internal static class SwiftTests
    {
        public const string Executor = "executor://visualswift/swiftpm/v1";
        public static readonly TestProperty Framework = TestProperty.Register("VisualSwift.TestFramework", "Swift Test Framework", typeof(string), typeof(TestCase));
        public static bool IsManifest(string source) => string.Equals(Path.GetFileName(source), "Package.swift", StringComparison.OrdinalIgnoreCase) && File.Exists(source);
        public static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(character);
                slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static IEnumerable<TestCase> Discover(string source, CancellationToken token, IMessageLogger logger)
        {
            source = Path.GetFullPath(source);
            var workspace = Path.GetDirectoryName(source);
            var toolchain = Toolchain.Resolve(workspace);
            logger.SendMessage(TestMessageLevel.Informational, "Discovering Swift tests: " + workspace);
            var description = Run(toolchain, "package --package-path " + Quote(workspace) + " describe --type json", workspace, token);
            EnsureSuccess(description, "Swift package inspection");
            var targets = JObject.Parse(description.Output)["targets"] as JArray;
            if (targets == null || !targets.Any(target => target.Value<string>("type") == "test")) return Array.Empty<TestCase>();
            var xctest = Run(toolchain, "test --package-path " + Quote(workspace) + " list --disable-swift-testing", workspace, token);
            EnsureSuccess(xctest, "Swift test discovery");
            var testing = Run(toolchain, "test --package-path " + Quote(workspace) + " list --skip-build --disable-xctest", workspace, token);
            EnsureSuccess(testing, "Swift Testing discovery");
            return ParseList(xctest.Output, "XCTest", source).Concat(ParseList(testing.Output, "Swift Testing", source)).ToArray();
        }

        private static IEnumerable<TestCase> ParseList(string output, string framework, string source)
        {
            foreach (var id in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Distinct())
            {
                if (!Regex.IsMatch(id, framework == "XCTest" ? @"^[^\s/]+/[^\s]+$" : @"^[^\s]+\([^\s]*\)$")) continue;
                var test = new TestCase(id, new Uri(Executor), source) { DisplayName = id };
                test.SetPropertyValue(Framework, framework);
                test.Traits.Add(new Trait("Framework", framework));
                test.Traits.Add(new Trait("Package", Path.GetFileName(Path.GetDirectoryName(source))));
                FindSource(test);
                yield return test;
            }
        }

        private static void FindSource(TestCase test)
        {
            var tests = Path.Combine(Path.GetDirectoryName(test.Source), "Tests");
            if (!Directory.Exists(tests)) return;
            var name = test.FullyQualifiedName.Substring(test.FullyQualifiedName.LastIndexOf('/') + 1).Split('(')[0];
            name = name.Substring(name.LastIndexOf('.') + 1);
            var expression = new Regex(@"\bfunc\s+" + Regex.Escape(name) + @"\s*\(");
            var module = test.FullyQualifiedName.Split('.')[0].Split('/')[0];
            var target = Path.Combine(tests, module);
            foreach (var file in Directory.EnumerateFiles(Directory.Exists(target) ? target : tests, "*.swift", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                    if (expression.IsMatch(lines[index])) { test.CodeFilePath = file; test.LineNumber = index + 1; return; }
            }
        }

        public static void Build(string source, CancellationToken token, IMessageLogger logger)
        {
            var workspace = Path.GetDirectoryName(source);
            logger.SendMessage(TestMessageLevel.Informational, "Building Swift tests: " + workspace);
            EnsureSuccess(Run(Toolchain.Resolve(workspace), "build --build-tests --package-path " + Quote(workspace), workspace, token), "Swift test build");
        }

        public static TestResult Execute(TestCase test, CancellationToken token, IMessageLogger logger)
        {
            var workspace = Path.GetDirectoryName(test.Source);
            var directory = Path.Combine(workspace, ".vs", "VisualSwift", "TestResults", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var xml = Path.Combine(directory, "results.xml");
            var framework = test.GetPropertyValue(Framework, "XCTest");
            var arguments = "test --package-path " + Quote(workspace) + " --skip-build --filter " + Quote("^" + Regex.Escape(test.FullyQualifiedName) + (framework == "XCTest" ? "$" : "")) +
                " --xunit-output " + Quote(xml) + (framework == "XCTest" ? " --disable-swift-testing --parallel --num-workers 1 --verbose" : " --disable-xctest");
            logger.SendMessage(TestMessageLevel.Informational, "Running " + test.FullyQualifiedName);
            var started = DateTimeOffset.UtcNow;
            var process = Run(Toolchain.Resolve(workspace), arguments, workspace, token);
            var result = ReadResult(test, xml, process.ExitCode, process.Output + Environment.NewLine + process.Error);
            result.StartTime = started;
            result.EndTime = DateTimeOffset.UtcNow;
            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, process.Output));
            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, process.Error));
            if (File.Exists(xml)) result.Attachments.Add(new AttachmentSet(new Uri(Executor), "SwiftPM xUnit results")
            { Attachments = { new UriDataAttachment(new Uri(xml), framework + " results") } });
            return result;
        }

        internal static TestResult ReadResult(TestCase test, string path, int exitCode, string output)
        {
            var result = new TestResult(test);
            if (!File.Exists(path)) { result.Outcome = TestOutcome.Failed; result.ErrorMessage = "SwiftPM produced no test results (exit " + exitCode + ")." + Environment.NewLine + output; return result; }
            XDocument document;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document = XDocument.Load(reader);
            var cases = document.Descendants().Where(element => element.Name.LocalName == "testcase").ToArray();
            if (cases.Length == 0) { result.Outcome = TestOutcome.Failed; result.ErrorMessage = "The selected test was not executed. Rediscover tests and retry." + Environment.NewLine + output; return result; }
            var failures = cases.SelectMany(element => element.Elements()).Where(element => element.Name.LocalName == "failure" || element.Name.LocalName == "error").ToArray();
            result.Outcome = failures.Length > 0 || exitCode != 0 ? TestOutcome.Failed : cases.All(element => element.Elements().Any(child => child.Name.LocalName == "skipped")) ? TestOutcome.Skipped : TestOutcome.Passed;
            if (result.Outcome == TestOutcome.Passed && test.GetPropertyValue(Framework, "XCTest") == "XCTest")
            {
                var method = test.FullyQualifiedName.Substring(test.FullyQualifiedName.IndexOf('.') + 1).Replace('/', '.');
                if (Regex.IsMatch(output, "Test Case '" + Regex.Escape(method) + "' skipped \\(")) result.Outcome = TestOutcome.Skipped;
            }
            result.Duration = TimeSpan.FromSeconds(cases.Sum(element => double.TryParse((string)element.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && !double.IsInfinity(seconds) ? seconds : 0));
            if (result.Outcome == TestOutcome.Failed)
            {
                var messages = failures.Select(element => ((string)element.Attribute("message") + " " + element.Value).Trim()).Where(value => value.Length > 0);
                result.ErrorMessage = string.Join(Environment.NewLine, messages);
                if (string.IsNullOrWhiteSpace(result.ErrorMessage) || result.ErrorMessage == "failure")
                {
                    var errors = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line.Contains(": error:"));
                    result.ErrorMessage = string.Join(Environment.NewLine, errors);
                    if (string.IsNullOrWhiteSpace(result.ErrorMessage)) result.ErrorMessage = output;
                }
                result.ErrorStackTrace = output;
            }
            return result;
        }

        private static void EnsureSuccess(CommandResult result, string action)
        { if (result.ExitCode != 0) throw new InvalidOperationException(action + " exited with code " + result.ExitCode + ": " + result.Error + Environment.NewLine + result.Output); }

        private static CommandResult Run(Toolchain toolchain, string arguments, string workspace, CancellationToken token)
            => RunAsync(toolchain, arguments, workspace, token).GetAwaiter().GetResult();

        private static async Task<CommandResult> RunAsync(Toolchain toolchain, string arguments, string workspace, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = toolchain.CreateSwiftStartInfo(arguments, workspace), EnableRaisingEvents = true })
            using (var job = new SwiftProcessJob())
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => exited.TrySetResult(true);
                if (!process.Start()) throw new InvalidOperationException("Swift could not be started.");
                job.Attach(process);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var completed = Task.WhenAll(exited.Task, output, error);
                if (await Task.WhenAny(completed, Task.Delay(TimeSpan.FromMinutes(10), token)).ConfigureAwait(false) != completed)
                {
                    job.Dispose();
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Swift test command exceeded 10 minutes.");
                }
                await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
        }

        private sealed class CommandResult
        {
            public CommandResult(int exitCode, string output, string error) { ExitCode = exitCode; Output = output; Error = error; }
            public int ExitCode { get; }
            public string Output { get; }
            public string Error { get; }
        }
    }
}
