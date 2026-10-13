using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace VisualSwift.Testing
{
    [FileExtension(".swift")]
    [DefaultExecutorUri(SwiftTests.Executor)]
    public sealed class SwiftTestDiscoverer : ITestDiscoverer
    {
        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            foreach (var source in sources.Where(SwiftTests.IsManifest).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var test in SwiftTests.Discover(source, CancellationToken.None, logger)) discoverySink.SendTestCase(test);
                }
                catch (Exception error) { logger.SendMessage(TestMessageLevel.Error, "Swift test discovery failed: " + error.Message); }
            }
        }
    }

    [ExtensionUri(SwiftTests.Executor)]
    public sealed class SwiftTestExecutor : ITestExecutor
    {
        private readonly object gate = new object();
        private CancellationTokenSource cancellation;
        public void Cancel() { lock (gate) cancellation?.Cancel(); }

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
            => Run(token => tests.Where(test => test.ExecutorUri.AbsoluteUri == SwiftTests.Executor).ToArray(), frameworkHandle);

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
            => Run(token => sources.Where(SwiftTests.IsManifest).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(source => SwiftTests.Discover(source, token, frameworkHandle)).ToArray(), frameworkHandle);

        private void Run(Func<CancellationToken, TestCase[]> discover, IFrameworkHandle handle)
        {
            using (var current = new CancellationTokenSource())
            {
                lock (gate) cancellation = current;
                try
                {
                    var tests = discover(current.Token);
                    foreach (var group in tests.GroupBy(test => test.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        current.Token.ThrowIfCancellationRequested();
                        Exception buildError = null;
                        try { SwiftTests.Build(group.Key, current.Token, handle); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { buildError = error; }
                        foreach (var test in group)
                        {
                            current.Token.ThrowIfCancellationRequested();
                            handle.RecordStart(test);
                            TestResult result;
                            try
                            {
                                if (buildError != null) throw new InvalidOperationException("Swift test build failed: " + buildError.Message);
                                result = SwiftTests.Execute(test, current.Token, handle);
                            }
                            catch (OperationCanceledException)
                            {
                                handle.RecordEnd(test, TestOutcome.None);
                                throw;
                            }
                            catch (Exception error) { result = new TestResult(test) { Outcome = TestOutcome.Failed, ErrorMessage = error.Message }; }
                            handle.RecordResult(result);
                            handle.RecordEnd(test, result.Outcome);
                        }
                    }
                }
                catch (OperationCanceledException) { handle.SendMessage(TestMessageLevel.Informational, "Swift test run cancelled."); }
                catch (Exception error) { handle.SendMessage(TestMessageLevel.Error, "Swift test run failed: " + error.Message); }
                finally { lock (gate) if (cancellation == current) cancellation = null; }
            }
        }
    }
}
