using Dalamud.Game.Network;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;
using Dalamud.Game.Command;
using Dalamud.IoC;

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

    [PluginService]
    private ICommandManager CommandManager { get; set; } = null!;

    private readonly Configuration _configuration;
    private bool _settingsVisible;
    private uint _fishCount;

    private Hook<PacketDispatcher.Delegates.HandleEventPlayPacket>? eventPlayPacketHook;

    public FishNotifyPlugin(
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider gameInteropProvider,
        IChatGui chat,
        IPluginLog pluginLog,
        ICommandManager commandManager)
    {
        PluginInterface = pluginInterface;
        GameInteropProvider = gameInteropProvider;
        Chat = chat;
        PluginLog = pluginLog;
        CommandManager = commandManager;

        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        unsafe
        {
            eventPlayPacketHook = GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventPlayPacket>(
                PacketDispatcher.Addresses.HandleEventPlayPacket.Value, DetourEventPlay);
            eventPlayPacketHook!.Enable();
        }

        CommandManager.AddHandler("/fishnotify", new CommandInfo(OnCommand)
        {
            HelpMessage = "FishNotify: open/close window or control chat alerts. Usage: /fishnotify [on|off]"
        });

        PluginInterface.UiBuilder.Draw += OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        eventPlayPacketHook?.Dispose();
        CommandManager.RemoveHandler("/fishnotify");
    }

    private unsafe void DetourEventPlay(ulong gameObjectId, uint eventId, ushort stage, ulong a4, uint* payload, byte payloadSize)
    {
        try
        {
            PluginLog.Debug(
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

                // The tug strength is encoded in the first parameter of the payload
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

    private void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();
        switch (a)
        {
            case "":
                // Toggle the settings window visibility
                _settingsVisible = !_settingsVisible;
                break;
            case "on":
                _configuration.ChatAlerts = true;
                PluginInterface.SavePluginConfig(_configuration);
                Chat.Print("[FishNotify] Chat alerts enabled.");
                break;
            case "off":
                _configuration.ChatAlerts = false;
                PluginInterface.SavePluginConfig(_configuration);
                Chat.Print("[FishNotify] Chat alerts disabled.");
                break;
            default:
                Chat.Print("[FishNotify] Usage: /fishnotify [on|off]");
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

            ImGui.TextColored(
                _fishCount == 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                $"Status: {(_fishCount == 0 ? "Unknown (not triggered yet)" : $"OK ({_fishCount} fish hooked)")}");
        }
        ImGui.End();
    }

    private void OnOpenConfigUi()
    {
        _settingsVisible = !_settingsVisible;
    }
}
