using System;
using System.Linq;

namespace ThermalDot;

// Deterministic failure-path checks; this backend never writes real Windows settings.
internal static class DisplayTimeoutTests
{
    public static void Run()
    {
        Check(DisplayTimeout.Label(null) == "暂不可用" && DisplayTimeout.Label(0) == "从不", "Unavailable is not never");
        Check(DisplayTimeout.Label(60) == "1 分钟" && DisplayTimeout.Label(90) == "90 秒", "Seconds must not be rounded");
        Check(DisplayTimeout.Choices(90).Any(c => c.Seconds == 90) && DisplayTimeout.Choices(0).Count(c => c.Seconds == 0) == 1, "Custom timeouts must survive");
        var api = new FakeApi(); var start = api.Read();
        var result = DisplayTimeout.Apply(start, 0, 1800, api);
        Check(result.Error == "" && api.Ac == 0 && api.Dc == 1800 && api.Writes == 1 && api.Activations == 1, "AC change must preserve DC and support never");
        api = new FakeApi(); start = api.Read();
        result = DisplayTimeout.Apply(start, 60, 300, api);
        Check(result.Error == "" && api.Ac == 60 && api.Dc == 300 && api.Writes == 1, "DC change must preserve AC");
        api = new FakeApi(); start = api.Read(); api.Dc = 900;
        result = DisplayTimeout.Apply(start, 300, 1800, api);
        Check(result.Error == "" && api.Dc == 900, "Untouched external DC change must survive");
        api = new FakeApi(); start = api.Read();
        result = DisplayTimeout.Apply(start, 60, 1800, api);
        Check(result.Error == "" && api.Writes == 0 && api.Activations == 0, "Unchanged values must not write or activate");
        api = new FakeApi(); start = api.Read(); api.Ac = 120;
        result = DisplayTimeout.Apply(start, 300, 1800, api);
        Check(result.Error != "" && api.Ac == 120 && api.Writes == 0, "Stale value must be rejected");
        api = new FakeApi(); start = api.Read(); api.Scheme = Guid.NewGuid();
        result = DisplayTimeout.Apply(start, 300, 1800, api);
        Check(result.Error != "" && api.Writes == 0 && api.Activations == 0, "Do not switch back to a stale plan");
        api = new FakeApi { FailDc = true }; start = api.Read();
        result = DisplayTimeout.Apply(start, 300, 600, api);
        Check(result.Error != "" && api.Ac == 60 && api.Dc == 1800, "A failed second write must restore the first");
        api = new FakeApi { FailActivate = true }; start = api.Read();
        result = DisplayTimeout.Apply(start, 300, 600, api);
        Check(result.Error != "" && api.Ac == 60 && api.Dc == 1800, "Activation failure must restore both stored values");
        api = new FakeApi { IgnoreWrite = true }; start = api.Read();
        result = DisplayTimeout.Apply(start, 300, 1800, api);
        Check(result.Error != "", "A successful API code without a matching readback is not success");
        api = new FakeApi { DenyRead = true };
        result = DisplayTimeout.Apply(new DisplayTimeoutState(), 0, 0, api);
        Check(result.Error != "" && api.Writes == 0, "Unavailable reads must prevent changes");
    }
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception("Screen timeout test: " + reason); }
    private sealed class FakeApi : IDisplayTimeoutApi
    {
        public Guid Scheme = Guid.NewGuid();
        public uint Ac = 60, Dc = 1800;
        public int Writes, Activations;
        public bool FailDc, FailActivate, IgnoreWrite, DenyRead;
        public DisplayTimeoutState Read() => DenyRead ? new() { Error = "Read denied" } : new() { Scheme = Scheme, AcSeconds = Ac, DcSeconds = Dc };
        public void Write(Guid scheme, bool ac, uint seconds)
        {
            Check(scheme == Scheme, "Unexpected target scheme");
            if (!ac && FailDc) throw new InvalidOperationException("DC denied");
            Writes++;
            if (!IgnoreWrite) { if (ac) Ac = seconds; else Dc = seconds; }
        }
        public void Activate(Guid scheme)
        {
            Check(scheme == Scheme, "Unexpected activation scheme");
            Activations++;
            if (FailActivate) throw new InvalidOperationException("Activation denied");
        }
    }
}