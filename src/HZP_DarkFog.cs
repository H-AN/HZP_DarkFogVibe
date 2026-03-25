using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace HZP_DarkFog;

[PluginMetadata(
    Id = "HZP_DarkFog",
    Version = "1.0.0",
    Name = "HZP_DarkFog",
    Author = "H-AN",
    Description = "Per-player team-based exposure control for human-vs-zombie gameplay."
)]
public sealed class HZP_DarkFog : BasePlugin
{
    private const string ConfigFileName = "HZP_DarkFog.jsonc";
    private const string ConfigSectionName = "HZP_DarkFogCFG";

    private readonly ILogger<HZP_DarkFog> _logger;

    private ServiceProvider? _serviceProvider;
    private IOptionsMonitor<HZP_DarkFog_Config>? _config;
    private IDisposable? _configChangeSubscription;
    private HZP_DarkFog_Service? _service;

    public HZP_DarkFog(ISwiftlyCore core) : base(core)
    {
        // _logger = core.LoggerFactory.CreateLogger<HZP_DarkFog>();
        _logger = NullLogger<HZP_DarkFog>.Instance;
    }

    public override void Load(bool hotReload)
    {
        _logger.LogInformation("HZP_DarkFog loading. hotReload={HotReload}", hotReload);

        Core.Configuration.InitializeJsonWithModel<HZP_DarkFog_Config>(ConfigFileName, ConfigSectionName).Configure(builder =>
        {
            builder.AddJsonFile(ConfigFileName, false, true);
        });

        var collection = new ServiceCollection();
        collection.AddSwiftly(Core);
        collection
            .AddOptionsWithValidateOnStart<HZP_DarkFog_Config>()
            .BindConfiguration(ConfigSectionName);

        _serviceProvider = collection.BuildServiceProvider();
        _config = _serviceProvider.GetRequiredService<IOptionsMonitor<HZP_DarkFog_Config>>();
        // _service = new HZP_DarkFog_Service(Core, Core.LoggerFactory.CreateLogger<HZP_DarkFog_Service>());
        _service = new HZP_DarkFog_Service(Core, NullLogger<HZP_DarkFog_Service>.Instance);
        _configChangeSubscription = _config.OnChange(OnConfigChanged);

        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapLoad += OnMapLoad;
        Core.Event.OnMapUnload += OnMapUnload;
        Core.Command.RegisterCommand("fog", HandleFogCommand, true);

        LogCurrentConfig("load");
        ApplyVisionForAllPlayersAfterDelay(hotReload ? 0.2f : 0.5f, hotReload ? "hot-reload" : "load");
    }

    public override void Unload()
    {
        _logger.LogInformation("HZP_DarkFog unloading.");

        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapLoad -= OnMapLoad;
        Core.Event.OnMapUnload -= OnMapUnload;

        _configChangeSubscription?.Dispose();
        _service?.ClearAll();
        _serviceProvider?.Dispose();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        _logger.LogInformation(
            "PlayerSpawn received for player {PlayerId}. Scheduling dark fog apply.",
            @event.UserId);
        ApplyVisionForPlayerAfterDelay(@event.UserId, 0.15f, "spawn");
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        _logger.LogInformation(
            "PlayerTeam received for player {PlayerId}. OldTeam={OldTeam} NewTeam={NewTeam}. Scheduling dark fog apply.",
            @event.UserId,
            @event.OldTeam,
            @event.Team);
        ApplyVisionForPlayerAfterDelay(@event.UserId, 0.15f, "team-change");
        return HookResult.Continue;
    }

    public void HandleFogCommand(ICommandContext context)
    {
        if (_service is null)
        {
            context.Reply("HZP_DarkFog service is unavailable.");
            return;
        }

        if (context.Args.Length < 2)
        {
            context.Reply("Usage: !fog <player-name|playerid|@me> <exposure|reset>");
            return;
        }

        var targetInput = string.Join(" ", context.Args[..^1]).Trim();
        var exposureInput = context.Args[^1].Trim();

        if (!TryResolveFogTarget(context, targetInput, out var target, out var errorMessage))
        {
            context.Reply(errorMessage);
            return;
        }

        var issuerName = context.Sender is IPlayer sender && sender.IsValid
            ? GetPlayerDisplayName(sender)
            : "Console";
        var targetName = GetPlayerDisplayName(target);

        if (string.Equals(exposureInput, "reset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exposureInput, "clear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exposureInput, "off", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Debug fog command reset player {TargetPlayerId} ({TargetPlayerName}). Issuer={Issuer}.",
                target.PlayerID,
                targetName,
                issuerName);

            _service.ResetPlayer(target);
            context.Reply($"Reset custom exposure for {targetName} (ID {target.PlayerID}).");
            return;
        }

        if (!TryParseExposure(exposureInput, out var parsedExposure))
        {
            context.Reply($"Invalid exposure value '{exposureInput}'. Example: !fog H-AN 0.01");
            return;
        }

        var appliedExposure = MathF.Max(0.0f, parsedExposure);
        var applied = _service.ApplyExposure(target, appliedExposure);
        if (!applied)
        {
            context.Reply($"Failed to apply exposure to {targetName} (ID {target.PlayerID}). Check plugin logs.");
            return;
        }

        _logger.LogInformation(
            "Debug fog command applied exposure {Exposure} to player {TargetPlayerId} ({TargetPlayerName}). IsFakeClient={IsFakeClient} Issuer={Issuer}.",
            appliedExposure,
            target.PlayerID,
            targetName,
            target.IsFakeClient,
            issuerName);

        context.Reply(
            $"Set {targetName} (ID {target.PlayerID}, Bot={target.IsFakeClient}) exposure to {appliedExposure.ToString("0.###", CultureInfo.InvariantCulture)}.");
    }

    private void OnConfigChanged(HZP_DarkFog_Config config)
    {
        _logger.LogInformation(
            "HZP_DarkFog config changed. Enable={Enable} HumanExposure={HumanExposure} ZombieExposure={ZombieExposure}",
            config.Enable,
            config.HumanExposure,
            config.ZombieExposure);
        ApplyVisionForAllPlayersAfterDelay(0.1f, "config-change");
    }

    private void ApplyVisionForPlayerAfterDelay(int playerId, float delaySeconds, string reason)
    {
        _logger.LogInformation(
            "Scheduling dark fog apply for player {PlayerId} after {DelaySeconds} seconds. Reason={Reason}",
            playerId,
            delaySeconds,
            reason);

        Core.Scheduler.DelayBySeconds(delaySeconds, () =>
        {
            var player = Core.PlayerManager.GetPlayer(playerId);
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                _logger.LogDebug(
                    "Skipped delayed dark fog apply for player {PlayerId} because the player is no longer valid.",
                    playerId);
                return;
            }

            var applied = ApplyVisionForCurrentTeam(player);
            _logger.LogInformation(
                "Delayed dark fog apply finished for player {PlayerId}. Applied={Applied}.",
                playerId,
                applied);
        });
    }

    private void ApplyVisionForAllPlayersAfterDelay(float delaySeconds, string reason)
    {
        _logger.LogInformation(
            "Scheduling dark fog apply for all players after {DelaySeconds} seconds. Reason={Reason}",
            delaySeconds,
            reason);

        Core.Scheduler.DelayBySeconds(delaySeconds, ApplyVisionForAllPlayers);
    }

    private void ApplyVisionForAllPlayers()
    {
        if (_service is null)
        {
            return;
        }

        var players = Core.PlayerManager.GetAllValidPlayers()
            .Where(static player => !player.IsFakeClient)
            .ToArray();
        _logger.LogInformation(
            "Applying dark fog to all current players. Count={PlayerCount}.",
            players.Length);

        foreach (var player in players)
        {
            ApplyVisionForCurrentTeam(player);
        }
    }

    private bool ApplyVisionForCurrentTeam(IPlayer? player)
    {
        if (_service is null || _config is null)
        {
            return false;
        }

        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var controller = player.Controller;
        if (controller is null || !controller.IsValid)
        {
            _logger.LogDebug(
                "Skipping dark fog apply for player {PlayerId} because the controller is invalid.",
                player.PlayerID);
            return false;
        }

        var config = _config.CurrentValue;
        if (!config.Enable)
        {
            _logger.LogInformation(
                "HZP_DarkFog is disabled in config. Resetting custom exposure for player {PlayerId}.",
                player.PlayerID);
            _service.ResetPlayer(player);
            return true;
        }

        var teamNum = controller.TeamNum;
        if (teamNum == (int)Team.CT)
        {
            _logger.LogInformation(
                "Applying configured human exposure {Exposure} to player {PlayerId} because current team is CT.",
                config.HumanExposure,
                player.PlayerID);
            _service.ApplyExposure(player, config.HumanExposure);
            return true;
        }

        if (teamNum == (int)Team.T)
        {
            _logger.LogInformation(
                "Applying configured zombie exposure {Exposure} to player {PlayerId} because current team is T.",
                config.ZombieExposure,
                player.PlayerID);
            _service.ApplyExposure(player, config.ZombieExposure);
            return true;
        }

        _logger.LogInformation(
            "Resetting custom exposure for player {PlayerId} because current team {TeamNum} is not CT/T.",
            player.PlayerID,
            teamNum);
        _service.ResetPlayer(player);
        return true;
    }

    private void LogCurrentConfig(string reason)
    {
        var config = _config?.CurrentValue;
        if (config is null)
        {
            return;
        }

        _logger.LogInformation(
            "HZP_DarkFog config snapshot. Reason={Reason} Enable={Enable} HumanExposure={HumanExposure} ZombieExposure={ZombieExposure}",
            reason,
            config.Enable,
            config.HumanExposure,
            config.ZombieExposure);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _logger.LogInformation(
            "Client disconnected for player {PlayerId}. Cleaning dark fog state.",
            @event.PlayerId);
        _service?.RemovePlayer(@event.PlayerId);
    }

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        _logger.LogInformation("MapLoad received. Scheduling dark fog apply for all players.");
        ApplyVisionForAllPlayersAfterDelay(1.0f, "map-load");
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        _logger.LogInformation("MapUnload received. Clearing active dark fog volumes.");
        _service?.ClearAllVolumes();
    }

    private bool TryResolveFogTarget(ICommandContext context, string input, out IPlayer target, out string errorMessage)
    {
        target = null!;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Usage: !fog <player-name|playerid|@me> <exposure|reset>";
            return false;
        }

        if (string.Equals(input, "@me", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Sender is IPlayer sender && sender.IsValid)
            {
                target = sender;
                return true;
            }

            errorMessage = "@me can only be used by an in-game player.";
            return false;
        }

        var players = Core.PlayerManager
            .GetAllPlayers()
            .Where(static player => player is not null && player.IsValid && !player.IsFakeClient)
            .ToList();

        if (players.Count == 0)
        {
            errorMessage = "No valid players are currently available.";
            return false;
        }

        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId))
        {
            var byPlayerId = players.FirstOrDefault(player => player.PlayerID == playerId);
            if (byPlayerId is not null)
            {
                target = byPlayerId;
                return true;
            }
        }

        var exactMatches = players
            .Where(player => string.Equals(GetPlayerDisplayName(player), input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            target = exactMatches[0];
            return true;
        }

        if (exactMatches.Count > 1)
        {
            errorMessage = $"Multiple players matched '{input}': {FormatPlayerMatchList(exactMatches)}";
            return false;
        }

        var partialMatches = players
            .Where(player => GetPlayerDisplayName(player).Contains(input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (partialMatches.Count == 1)
        {
            target = partialMatches[0];
            return true;
        }

        if (partialMatches.Count > 1)
        {
            errorMessage = $"Multiple players matched '{input}': {FormatPlayerMatchList(partialMatches)}";
            return false;
        }

        errorMessage = $"No player matched '{input}'.";
        return false;
    }

    private static bool TryParseExposure(string input, out float exposure)
    {
        if (float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out exposure))
        {
            return true;
        }

        return float.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out exposure);
    }

    private static string FormatPlayerMatchList(IEnumerable<IPlayer> players)
    {
        return string.Join(
            ", ",
            players
                .Take(5)
                .Select(player => $"{GetPlayerDisplayName(player)}(ID {player.PlayerID}, Bot={player.IsFakeClient})"));
    }

    private static string GetPlayerDisplayName(IPlayer player)
    {
        var controller = player.Controller;
        if (controller is not null
            && controller.IsValid
            && !string.IsNullOrWhiteSpace(controller.PlayerName))
        {
            return controller.PlayerName;
        }

        return $"#{player.PlayerID}";
    }
}
