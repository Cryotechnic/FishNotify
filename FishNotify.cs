using Dalamud.Game.Network;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace FishNotify;

public sealed class FishNotifyPlugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface PluginInterface;
    private readonly IGameInteropProvider GameInteropProvider;
    private readonly IChatGui Chat;
    private readonly IPluginLog PluginLog;

    private readonly Configuration _configuration;
    private bool _settingsVisible;
    private int _expectedOpCode = -1;
    private uint _fishCount;

    private Hook<PacketDispatcher.Delegates.HandleEventPlayPacket>? eventPlayPacketHook;

    public FishNotifyPlugin(
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider gameInteropProvider,
        IChatGui chat,
        IPluginLog pluginLog)
    {
        PluginInterface = pluginInterface;
        GameInteropProvider = gameInteropProvider;
        Chat = chat;
        PluginLog = pluginLog;

        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        unsafe
        {
            eventPlayPacketHook = GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventPlayPacket>(
                PacketDispatcher.Addresses.HandleEventPlayPacket.Value, DetourEventPlay);
            eventPlayPacketHook!.Enable();
        }

        PluginInterface.UiBuilder.Draw += OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        eventPlayPacketHook?.Dispose();
    }

    private unsafe void DetourEventPlay(ulong gameObjectId, uint eventId, ushort stage, ulong a4, uint* payload, byte payloadSize)
    {
        try
        {
            PluginLog.Information(
                "DetourEventPlay called with GameObjectId: {0:X}, EventId: {1:X8}, Stage: {2}, PayloadSize: {3}",
                gameObjectId, eventId, stage, payloadSize);

            // Always call the original to avoid breaking game's event flow
            eventPlayPacketHook?.Original(gameObjectId, eventId, stage, a4, payload, payloadSize);

            if (eventId != 0x00150001)
                return;

            // Stage 5 = "fish hooked". In practice payloadSize here is the param count,
            // and we typically get exactly 1 param for tug strength at index 0
            if (stage == 5 && payload != null && payloadSize >= 1)
            {
                var p0 = payload[0];

                // Handle both opcode values and ordinal encodings defensively
                var tug = p0 switch
                {
                    0x124 or 1 => "light",
                    0x125 or 2 => "medium",
                    0x126 or 3 => "heavy",
                    _ => null
                };

                if (tug != null)
                {
                    ++_fishCount;
                    switch (tug)
                    {
                        case "light":
                            Sounds.PlaySound(Resources.Info);
                            break;
                        case "medium":
                            Sounds.PlaySound(Resources.Alert);
                            break;
                        case "heavy":
                            Sounds.PlaySound(Resources.Alarm);
                            break;
                    }
                    SendChatAlert(tug);
                }
                else
                {
                    PluginLog.Debug($"Unexpected tug value at stage 5: 0x{p0:X}");
                }
            }

            // Do not stop sounds on other stages
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "DetourEventPlay failed");
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
