using DemoTracer;

namespace DemoTracer.Tests;

public sealed class UtilityTracePathTests
{
    [Fact]
    public void TraceFileNamesStayUnderPluginTraceDirectory()
    {
        var moduleDirectory = Path.GetFullPath(Path.Combine("test-data", "plugin"));

        var resolved = DemoTracerPlugin.ResolveUtilityTracePathUnder(
            moduleDirectory,
            "utility.csv");

        Assert.Equal(
            Path.Combine(moduleDirectory, "traces", "utility.csv"),
            resolved);
    }

    [Theory]
    [InlineData("../outside.csv")]
    [InlineData("..\\outside.csv")]
    [InlineData("nested/trace.csv")]
    [InlineData("nested\\trace.csv")]
    [InlineData("C:\\trace.csv")]
    [InlineData("trace.txt")]
    public void TraceFileNamesRejectEscapesAndNonCsvTargets(string requestedPath)
    {
        var moduleDirectory = Path.GetFullPath(Path.Combine("test-data", "plugin"));

        Assert.Throws<InvalidDataException>(() =>
            DemoTracerPlugin.ResolveUtilityTracePathUnder(moduleDirectory, requestedPath));
    }
}
