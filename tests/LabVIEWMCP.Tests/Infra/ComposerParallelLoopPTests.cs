using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A parallel For Loop's P terminal, wired through <c>parin="&lt;net&gt;"</c> (our attribute; NI's
/// format has none and NI's exporter drops the wire). Iteration parallelism itself is the
/// <c>&lt;?lvmcp parallel?&gt;</c> instruction after <c>&lt;/VI&gt;</c>; until this attribute the
/// composer always left P unwired. The composed VI is read back with pylabview: the outer
/// <c>parForWorkers</c> terminal must sit on a signal, the static instance count must be kept, and
/// the reader must write <c>parin</c> back. A <c>parin</c> on a loop that is not parallel is
/// refused. SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ComposerParallelLoopPTests
{
    private const string Probe = """
        <VI _name="Parallel P Probe.vi" description="A parallel For Loop whose P terminal is wired.">
          <Control _name="N" type="int32" uid="4301" uid_parent="root" value="10" outputs="value:4301.value" />
          <Control _name="P" type="int32" uid="4302" uid_parent="root" value="4" outputs="value:4302.value" />
          <Structure _name="For Loop" count="4310.value" maxin="4301.value" maxout="" parin="4302.value" uid="4310" uid_parent="root">
            <Tunnel _id="Out1" inputs="value:4310.value" mode="index" outputs="value:4311.value" uid="4311" uid_parent="4310" />
          </Structure>
          <Indicator _name="i" type="array{int32}" uid="4320" uid_parent="root" inputs="value:4311.value" />
        </VI>
        <?lvmcp parallel uid="4310" workers="8"?>
        """;

    [Fact]
    public async Task A_wired_P_terminal_is_on_a_signal_and_reads_back_as_parin()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "parforp-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Parallel P Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(composed.Written, composed.Message);

            Directory.CreateDirectory(Path.Combine(dir, "x"));
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "ParallelPProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var loop = heap.Descendants("SL__arrayElement")
                .Single(n => (string?)n.Attribute("class") == "forLoop");
            Assert.Equal(8, Convert.ToInt32((string?)loop.Element("ParForNumStaticWorkers"), 16));
            // the OUTER P terminal is the loop's own term whose dco is the parForWorkers DCO
            var outer = loop.Element("termList")!.Elements()
                .Single(t => (string?)t.Element("dco")?.Attribute("class") == "parForWorkers");
            var uid = (string?)outer.Attribute("uid");
            Assert.Contains(heap.Descendants("SL__arrayElement")
                    .Where(s => (string?)s.Attribute("class") == "signal"),
                s => s.Element("termList")!.Elements().Any(e => (string?)e.Attribute("uid") == uid));

            var read = Path.Combine(dir, "read.xml");
            var back = await AixmlComposer.ReadAsync(setup, vi, read, 120, default);
            Assert.True(back.Written, back.Message);
            var text = await File.ReadAllTextAsync(read);
            var forLoop = XElement.Parse(text[..(text.IndexOf("</VI>", StringComparison.Ordinal) + 5)])
                .Descendants("Structure").Single(s => (string?)s.Attribute("_name") == "For Loop");
            Assert.False(string.IsNullOrEmpty((string?)forLoop.Attribute("parin")));
            Assert.Contains("<?lvmcp parallel", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Parin_on_a_loop_that_is_not_parallel_is_refused()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "parforp-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Parallel P Probe.vi");
            await File.WriteAllTextAsync(xml, Probe.Replace("<?lvmcp parallel uid=\"4310\" workers=\"8\"?>", ""));
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.False(composed.Written);
            Assert.Contains("parin", composed.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
