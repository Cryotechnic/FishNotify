using Dalamud.Game.Network;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace FishNotify;

public sealed class FishNotifyPlugin : IDalamudPlugin
{
    [PluginService]
    private IDalamudPluginInterface PluginInterface { get; set; } = null!;
    
    [PluginService]
    private IGameInteropProvider GameInteropProvider { get; set; } = null!;

    [PluginService]
    private IChatGui Chat { get; set; } = null!;

    [PluginService]
    private IPluginLog PluginLog { get; set; } = null!;

    private readonly Configuration _configuration;
    private bool _settingsVisible;
    private int _expectedOpCode = -1;
    private uint _fishCount;

    private Hook<PacketDispatcher.Delegates.HandleEventPlayPacket>? eventPlayPacketHook;

    public FishNotifyPlugin()
    {
        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        unsafe
        {
            eventPlayPacketHook = GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventPlayPacket>(
                PacketDispatcher.Addresses.HandleEventPlayPacket.Value, DetourEventPlay);   
            eventPlayPacketHook!.Enable();
        }
        
        PluginInterface.UiBuilder.Draw += OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;

        var client = new HttpClient();
        client.GetStringAsync("https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json")
            .ContinueWith(ExtractOpCode);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
    }

    private void ExtractOpCode(Task<string> task)
    {
        try
        {
            var regions = JsonConvert.DeserializeObject<List<OpcodeRegion>>(task.Result);
            if (regions == null)
            {
                PluginLog.Warning("No regions found in opcode list");
                return;
            }

            var region = regions.Find(r => r.Region == "Global");
            if (region == null || region.Lists == null)
            {
                PluginLog.Warning("No global region found in opcode list");
                return;
            }

            if (!region.Lists.TryGetValue("ServerZoneIpcType", out List<OpcodeList>? serverZoneIpcTypes) || serverZoneIpcTypes == null)
            {
                PluginLog.Warning("No ServerZoneIpcType in opcode list");
                return;
            }

            var eventPlay = serverZoneIpcTypes.Find(opcode => opcode.Name == "EventPlay");
            if (eventPlay == null)
            {
                PluginLog.Warning("No EventPlay opcode in ServerZoneIpcType");
                return;
            }

            _expectedOpCode = eventPlay.Opcode;
            PluginLog.Debug($"Found EventPlay opcode {_expectedOpCode:X4}");
        }
        catch (Exception e)
        {
            PluginLog.Error(e, "Could not download/extract opcodes: {}", e.Message);
        }
    }

    private unsafe void DetourEventPlay(ulong gameObjectId, uint eventId, ushort stage, ulong a4, uint* payload,
        byte payloadSize)
    {
        PluginLog.Verbose("DetourEventPlay called with GameObjectId: {0:X}, EventId: {1:X8}, Stage: {2}, PayloadSize: {3}",
            gameObjectId, eventId, stage, payloadSize);
        if (_expectedOpCode < 0)
            return;
    }

    private void OnNetworkMessage(IntPtr dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
    {
        if (direction != NetworkMessageDirection.ZoneDown || opCode != _expectedOpCode)
            return;

        var data = new byte[32];
        Marshal.Copy(dataPtr, data, 0, data.Length);

        int eventId = BitConverter.ToInt32(data, 8);
        short scene = BitConverter.ToInt16(data, 12);
        int param5 = BitConverter.ToInt32(data, 28);

        // Fishing event?
        if (eventId != 0x00150001)
            return;

        // Fish hooked?
        if (scene != 5)
            return;

        switch (param5)
        {

            case 0x124:
                // light tug (!)
                ++_fishCount;
                Sounds.PlaySound(Resources.Info);
                SendChatAlert("light");
                break;

            case 0x125:
                // medium tug (!!)
                ++_fishCount;
                Sounds.PlaySound(Resources.Alert);
                SendChatAlert("medium");
                break;

            case 0x126:
                // heavy tug (!!!)
                ++_fishCount;
                Sounds.PlaySound(Resources.Alarm);
                SendChatAlert("heavy");
                break;

            default:
                Sounds.Stop();
                break;
        }
    }

    private void SendChatAlert(string size)
    {
        if (!_configuration.ChatAlerts)
        {
            return;
        }

        SeString message = new SeStringBuilder()
            .AddUiForeground(514)
            .Append("[FishNotify]")
            .AddUiForegroundOff()
            .Append(" You hook a fish with a ")
            .AddUiForeground(514)
            .Append(size)
            .AddUiForegroundOff()
            .Append(" bite.")
            .Build();
        Chat.Print(message);
    }

    private void OnDrawUI()
    {
        if (!_settingsVisible)
            return;

        if (ImGui.Begin("FishNotify", ref _settingsVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var chatAlerts = _configuration.ChatAlerts;
            if (ImGui.Checkbox("Show chat message on hooking a fish", ref chatAlerts))
            {
                _configuration.ChatAlerts = chatAlerts;
                PluginInterface.SavePluginConfig(_configuration);
            }

            if (_expectedOpCode > -1)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Status: {(_fishCount == 0 ? "Unknown (not triggered yet)" : $"OK ({_fishCount} fish hooked)")}, opcode = {_expectedOpCode:X}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, "Status: No opcode :(");
        }
        ImGui.End();
    }

    private void OnOpenConfigUi()
    {
        _settingsVisible = !_settingsVisible;
    }
}
