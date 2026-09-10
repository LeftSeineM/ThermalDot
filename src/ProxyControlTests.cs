using System;
using System.Text.Json;
namespace ThermalDot;
internal static class ProxyControlTests
{
 static void Check(bool ok) { if (!ok) throw new Exception("Proxy control validation failed"); }
 internal static void Run()
 {
  using var config=JsonDocument.Parse("""{"mode":"rule","mixed-port":7890}""");
  using var proxies=JsonDocument.Parse("""
  {"GLOBAL":{"type":"Selector","all":["美国Y01","美国Y02"],"now":"美国Y01"},
   "Final":{"type":"Selector","all":["Main"],"now":"Main"},
   "Main":{"type":"Selector","all":["美国Y01","美国Y02"],"now":"美国Y01"},
   "Domestic":{"type":"Selector","all":["DIRECT"],"now":"DIRECT"},
   "美国Y01":{"type":"Shadowsocks"},"美国Y02":{"type":"Shadowsocks"}}
  """);
  using var rules=JsonDocument.Parse("""[{"type":"Match","proxy":"Final"}]""");
  var state=ProxyControl.Parse(config.RootElement,proxies.RootElement,rules.RootElement);
  Check(state.Ready && state.Nodes.Count==2 && state.Selections.Count==2 && state.Selections.ContainsKey("Main") && state.SelectedNode=="美国Y01");
  state.System=new(3,"127.0.0.1:7890","<local>","");
  Check(ProxyControl.Matches(state,state));
  var other=ProxyControl.Parse(config.RootElement,proxies.RootElement,rules.RootElement);
  other.System=state.System; other.Selections["Main"]="美国Y02";
  Check(!ProxyControl.Matches(other,state));
  using var tun=JsonDocument.Parse("""{"mode":"rule","mixed-port":7890,"tun":{"enable":true}}""");
  Check(!ProxyControl.Parse(tun.RootElement,proxies.RootElement,rules.RootElement).Ready);
  Check(SystemProxy.IsClash(state.System,7890));
  Check(SystemProxy.IsOff(state.System with {Flags=1}));
  Check(!SystemProxy.IsOff(state.System with {Flags=5}));
  Check(!SystemProxy.IsOff(state.System with {Flags=9}));
  Check(ProxyControl.NodeLabel("🇺🇲 美国Y02 | IEPL | x1.5",1)=="美国Y02");
 }
}