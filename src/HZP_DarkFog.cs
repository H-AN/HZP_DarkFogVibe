using System.Globalization;
using HanZombiePlagueS2;
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
    Description = "Per-player exposure control for zombie-plague gameplay based on HanZombiePlague API."
)]
public sealed class HZP_DarkFog : BasePlugin
{
    private const string ConfigFileName = "HZP_DarkFog.jsonc";
    private const string ConfigSectionName = "HZP_DarkFogCFG";
    private const string ZombiePlagueInterfaceName = "HanZombiePlague";
    private const string DefaultAdminCommandName = "fog";

    private readonly ILogger<HZP_DarkFog> _logger;

    private ServiceProvider? _serviceProvider;
    private IOptionsMonitor<HZP_DarkFog_Config>? _config;
    private IDisposable? _configChangeSubscription;
    private HZP_DarkFog_Service? _service;
    private IHanZombiePlagueAPI? _zpApi;

    private string? _registeredAdminCommandName;
    private string? _registeredHiddenCommandName;

    private readonly Dictionary<int, float> _manualExposureOverrideByPlayerId = [];
    private readonly Dictionary<string, float> _zombieGroupExposureByClassName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _resolvedZombieExposureByClassCache = new(StringComparer.OrdinalIgnoreCase);

    public HZP_DarkFog(ISwiftlyCore core) : base(core)
    {
        _logger = NullLogger<HZP_DarkFog>.Instance;
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (!interfaceManager.HasSharedInterface(ZombiePlagueInterfaceName))
        {
            throw new Exception($"[HZP_DarkFog] Missing dependency: {ZombiePlagueInterfaceName}");
        }

        AttachZombiePlagueApi(interfaceManager.GetSharedInterface<IHanZombiePlagueAPI>(ZombiePlagueInterfaceName));

        if (_zpApi is null)
        {
            throw new Exception($"[HZP_DarkFog] Failed to load {ZombiePlagueInterfaceName} API.");
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (!interfaceManager.HasSharedInterface(ZombiePlagueInterfaceName))
        {
            return;
        }

        AttachZombiePlagueApi(interfaceManager.GetSharedInterface<IHanZombiePlagueAPI>(ZombiePlagueInterfaceName));
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
        _service = new HZP_DarkFog_Service(Core, NullLogger<HZP_DarkFog_Service>.Instance);
        _configChangeSubscription = _config.OnChange(OnConfigChanged);

        var currentConfig = _config.CurrentValue;
        RebuildZombieGroupExposureCache(currentConfig);
        RegisterConfiguredCommands(currentConfig);

        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapLoad += OnMapLoad;
        Core.Event.OnMapUnload += OnMapUnload;

        LogCurrentConfig("load");
        ApplyVisionForAllPlayersAfterDelay(hotReload ? 0.2f : 0.5f, hotReload ? "hot-reload" : "load");
    }

    public override void Unload()
    {
        _logger.LogInformation("HZP_DarkFog unloading.");

        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapLoad -= OnMapLoad;
        Core.Event.OnMapUnload -= OnMapUnload;

        UnregisterConfiguredCommands();
        DetachZombiePlagueApi();

        _configChangeSubscription?.Dispose();
        _service?.ClearAll();
        _serviceProvider?.Dispose();

        _manualExposureOverrideByPlayerId.Clear();
        _zombieGroupExposureByClassName.Clear();
        _resolvedZombieExposureByClassCache.Clear();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        ApplyVisionForPlayerAfterDelay(@event.UserId, 0.15f, "spawn");
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        ApplyVisionForPlayerAfterDelay(@event.UserId, 0.15f, "team-change");
        return HookResult.Continue;
    }

    public void HandleFogCommand(ICommandContext context)
    {
        if (_service is null)
        {
            context.Reply(T(context, "DarkFog.ErrorServiceUnavailable"));
            return;
        }

        if (context.Args.Length < 2)
        {
            context.Reply(T(context, "DarkFog.Admin.Usage", context.CommandName));
            return;
        }

        var targetInput = string.Join(" ", context.Args[..^1]).Trim();
        var exposureInput = context.Args[^1].Trim();

        if (!TryResolveFogTarget(context, targetInput, out var target, out var errorMessage))
        {
            context.Reply(errorMessage);
            return;
        }

        var targetName = GetPlayerDisplayName(target);

        if (IsResetKeyword(exposureInput))
        {
            RemoveManualExposureOverride(target.PlayerID);
            var resetApplied = ApplyVisionForCurrentRole(target);
            if (!resetApplied)
            {
                context.Reply(T(context, "DarkFog.Admin.ApplyFailed", targetName, target.PlayerID));
                return;
            }

            context.Reply(T(context, "DarkFog.Admin.ResetSuccess", targetName, target.PlayerID));
            return;
        }

        if (!TryParseExposure(exposureInput, out var parsedExposure))
        {
            context.Reply(T(context, "DarkFog.Admin.InvalidExposure", exposureInput, context.CommandName));
            return;
        }

        var appliedExposure = MathF.Max(0.0f, parsedExposure);
        SetManualExposureOverride(target.PlayerID, appliedExposure);

        var applied = ApplyVisionForCurrentRole(target);
        if (!applied)
        {
            context.Reply(T(context, "DarkFog.Admin.ApplyFailed", targetName, target.PlayerID));
            return;
        }

        context.Reply(
            T(
                context,
                "DarkFog.Admin.SetSuccess",
                targetName,
                target.PlayerID,
                appliedExposure.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    public void HandleHiddenExposureCommand(ICommandContext context)
    {
        if (_service is null)
        {
            return;
        }

        if (context.Sender is not IPlayer sender || !sender.IsValid)
        {
            return;
        }

        if (context.Args.Length != 1)
        {
            return;
        }

        var exposureInput = context.Args[0].Trim();

        if (IsResetKeyword(exposureInput))
        {
            _ = ApplyVisionForCurrentRole(sender);
            return;
        }

        if (!TryParseExposure(exposureInput, out var parsedExposure))
        {
            return;
        }

        var appliedExposure = MathF.Max(0.0f, parsedExposure);
        _ = _service.ApplyExposure(sender, appliedExposure);
    }

    private void OnConfigChanged(HZP_DarkFog_Config config)
    {
        _logger.LogInformation(
            "HZP_DarkFog config changed. Enable={Enable} HumanExposure={HumanExposure} ZombieExposure={ZombieExposure} ZombieGroupCount={ZombieGroupCount}",
            config.Enable,
            config.HumanExposure,
            config.ZombieExposure,
            config.ZombieGroups?.Count ?? 0);

        Core.Scheduler.NextWorldUpdate(() =>
        {
            var latestConfig = _config?.CurrentValue ?? config;
            RebuildZombieGroupExposureCache(latestConfig);
            RegisterConfiguredCommands(latestConfig);
            LogCurrentConfig("config-change");
            ApplyVisionForAllPlayersAfterDelay(0.1f, "config-change");
        });
    }

    private void ApplyVisionForPlayerAfterDelay(int playerId, float delaySeconds, string reason)
    {
        Core.Scheduler.DelayBySeconds(delaySeconds, () =>
        {
            var player = Core.PlayerManager.GetPlayer(playerId);
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            var applied = ApplyVisionForCurrentRole(player);
            _logger.LogDebug(
                "Delayed dark fog apply finished for player {PlayerId}. Reason={Reason} Applied={Applied}",
                playerId,
                reason,
                applied);
        });
    }

    private void ApplyVisionForAllPlayersAfterDelay(float delaySeconds, string reason)
    {
        _logger.LogDebug(
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

        foreach (var player in players)
        {
            ApplyVisionForCurrentRole(player);
        }
    }

    private bool ApplyVisionForCurrentRole(IPlayer? player)
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
            return false;
        }

        var config = _config.CurrentValue;
        if (!config.Enable)
        {
            _service.ResetPlayer(player);
            return true;
        }

        if (_manualExposureOverrideByPlayerId.TryGetValue(player.PlayerID, out var manualExposure))
        {
            return _service.ApplyExposure(player, MathF.Max(0.0f, manualExposure));
        }

        if (_zpApi is null)
        {
            _service.ResetPlayer(player);
            return true;
        }

        bool isZombie;
        try
        {
            isZombie = _zpApi.HZP_IsZombie(player.PlayerID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query zombie state for player {PlayerId}.", player.PlayerID);
            _service.ResetPlayer(player);
            return true;
        }

        if (!isZombie)
        {
            return _service.ApplyExposure(player, MathF.Max(0.0f, config.HumanExposure));
        }

        var zombieClassName = ResolveZombieClassName(player);
        var exposure = ResolveZombieExposure(zombieClassName, MathF.Max(0.0f, config.ZombieExposure));
        return _service.ApplyExposure(player, exposure);
    }

    private string ResolveZombieClassName(IPlayer player)
    {
        if (_zpApi is null)
        {
            return string.Empty;
        }

        try
        {
            var rawClassName = _zpApi.HZP_GetZombieClassname(player);
            var className = rawClassName?.Trim() ?? string.Empty;
            return className;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query zombie class name for player {PlayerId}.", player.PlayerID);
            return string.Empty;
        }
    }

    private float ResolveZombieExposure(string zombieClassName, float fallbackExposure)
    {
        if (string.IsNullOrWhiteSpace(zombieClassName))
        {
            return fallbackExposure;
        }

        if (_resolvedZombieExposureByClassCache.TryGetValue(zombieClassName, out var cachedExposure))
        {
            return cachedExposure;
        }

        var exposure = _zombieGroupExposureByClassName.TryGetValue(zombieClassName, out var groupedExposure)
            ? groupedExposure
            : fallbackExposure;

        _resolvedZombieExposureByClassCache[zombieClassName] = exposure;
        return exposure;
    }

    private void RebuildZombieGroupExposureCache(HZP_DarkFog_Config config)
    {
        _zombieGroupExposureByClassName.Clear();
        _resolvedZombieExposureByClassCache.Clear();

        foreach (var group in config.ZombieGroups ?? [])
        {
            if (group is null || !group.Enable)
            {
                continue;
            }

            var className = group.ZombieClassName?.Trim();
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            _zombieGroupExposureByClassName[className] = MathF.Max(0.0f, group.Exposure);
        }

        _logger.LogInformation(
            "Rebuilt zombie exposure group cache. GroupCount={GroupCount}",
            _zombieGroupExposureByClassName.Count);
    }

    private void RegisterConfiguredCommands(HZP_DarkFog_Config config)
    {
        UnregisterConfiguredCommands();

        var adminCommandName = NormalizeCommandName(config.AdminCommandName, DefaultAdminCommandName);
        var adminPermission = NormalizePermission(config.AdminCommandPermission);

        if (string.IsNullOrWhiteSpace(adminPermission))
        {
            Core.Command.RegisterCommand(adminCommandName, HandleFogCommand, true);
            _logger.LogWarning(
                "Admin command '{CommandName}' is registered without permission restriction. Consider setting AdminCommandPermission.",
                adminCommandName);
        }
        else
        {
            Core.Command.RegisterCommand(adminCommandName, HandleFogCommand, true, adminPermission);
        }

        _registeredAdminCommandName = adminCommandName;

        if (!config.HiddenExposureCommandEnabled)
        {
            return;
        }

        var hiddenCommandName = NormalizeCommandName(config.HiddenExposureCommandName, string.Empty);
        if (string.IsNullOrWhiteSpace(hiddenCommandName))
        {
            _logger.LogWarning("Hidden exposure command is enabled, but HiddenExposureCommandName is empty.");
            return;
        }

        if (string.Equals(hiddenCommandName, adminCommandName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Hidden command '{HiddenCommandName}' conflicts with admin command '{AdminCommandName}'. Hidden command registration skipped.",
                hiddenCommandName,
                adminCommandName);
            return;
        }

        Core.Command.RegisterCommand(hiddenCommandName, HandleHiddenExposureCommand, true);
        _registeredHiddenCommandName = hiddenCommandName;
    }

    private void UnregisterConfiguredCommands()
    {
        UnregisterCommandIfPresent(ref _registeredAdminCommandName);
        UnregisterCommandIfPresent(ref _registeredHiddenCommandName);
    }

    private void UnregisterCommandIfPresent(ref string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return;
        }

        try
        {
            Core.Command.UnregisterCommand(commandName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to unregister command '{CommandName}'.", commandName);
        }

        commandName = null;
    }

    private static string NormalizeCommandName(string? rawCommandName, string fallback)
    {
        var commandName = string.IsNullOrWhiteSpace(rawCommandName)
            ? fallback
            : rawCommandName.Trim();

        return commandName.TrimStart('!', '/').Trim();
    }

    private static string NormalizePermission(string? rawPermission)
    {
        return string.IsNullOrWhiteSpace(rawPermission)
            ? string.Empty
            : rawPermission.Trim();
    }

    private void LogCurrentConfig(string reason)
    {
        var config = _config?.CurrentValue;
        if (config is null)
        {
            return;
        }

        _logger.LogInformation(
            "HZP_DarkFog config snapshot. Reason={Reason} Enable={Enable} HumanExposure={HumanExposure} ZombieExposure={ZombieExposure} AdminCommand={AdminCommand} AdminPermission={AdminPermission} HiddenEnabled={HiddenEnabled} HiddenCommand={HiddenCommand} ZombieGroupCount={ZombieGroupCount}",
            reason,
            config.Enable,
            config.HumanExposure,
            config.ZombieExposure,
            config.AdminCommandName,
            config.AdminCommandPermission,
            config.HiddenExposureCommandEnabled,
            config.HiddenExposureCommandName,
            config.ZombieGroups?.Count ?? 0);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _service?.RemovePlayer(@event.PlayerId);
        RemoveManualExposureOverride(@event.PlayerId);
    }

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        ApplyVisionForAllPlayersAfterDelay(1.0f, "map-load");
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        _service?.ClearAllVolumes();
        _manualExposureOverrideByPlayerId.Clear();
    }

    private bool TryResolveFogTarget(ICommandContext context, string input, out IPlayer target, out string errorMessage)
    {
        target = null!;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = T(context, "DarkFog.Admin.Usage", context.CommandName);
            return false;
        }

        if (string.Equals(input, "@me", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Sender is IPlayer sender && sender.IsValid)
            {
                target = sender;
                return true;
            }

            errorMessage = T(context, "DarkFog.Target.MePlayerOnly");
            return false;
        }

        var players = Core.PlayerManager
            .GetAllPlayers()
            .Where(static player => player is not null && player.IsValid && !player.IsFakeClient)
            .ToList();

        if (players.Count == 0)
        {
            errorMessage = T(context, "DarkFog.Target.NoPlayersAvailable");
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
            errorMessage = T(context, "DarkFog.Target.MultipleMatches", input, FormatPlayerMatchList(exactMatches));
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
            errorMessage = T(context, "DarkFog.Target.MultipleMatches", input, FormatPlayerMatchList(partialMatches));
            return false;
        }

        errorMessage = T(context, "DarkFog.Target.NotFound", input);
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
                .Select(player => $"{GetPlayerDisplayName(player)}(ID {player.PlayerID})"));
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

    private void SetManualExposureOverride(int playerId, float exposure)
    {
        _manualExposureOverrideByPlayerId[playerId] = MathF.Max(0.0f, exposure);
    }

    private void RemoveManualExposureOverride(int playerId)
    {
        _manualExposureOverrideByPlayerId.Remove(playerId);
    }

    private static bool IsResetKeyword(string input)
    {
        return input.Equals("reset", StringComparison.OrdinalIgnoreCase)
            || input.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || input.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private void AttachZombiePlagueApi(IHanZombiePlagueAPI? zpApi)
    {
        if (ReferenceEquals(_zpApi, zpApi))
        {
            return;
        }

        DetachZombiePlagueApi();
        _zpApi = zpApi;

        if (_zpApi is null)
        {
            _logger.LogWarning("HanZombiePlague API is unavailable.");
            return;
        }

        _zpApi.HZP_OnGameStart += OnZombieGameStart;
        _zpApi.HZP_OnPlayerInfect += OnZombiePlayerInfect;
        _zpApi.HZP_OnMotherZombieSelected += OnZombieRoleSelected;
        _zpApi.HZP_OnNemesisSelected += OnZombieRoleSelected;
        _zpApi.HZP_OnAssassinSelected += OnZombieRoleSelected;
        _zpApi.HZP_OnHeroSelected += OnZombieRoleSelected;
        _zpApi.HZP_OnSurvivorSelected += OnZombieRoleSelected;
        _zpApi.HZP_OnSniperSelected += OnZombieRoleSelected;

        _logger.LogInformation("Attached HanZombiePlague API.");
    }

    private void DetachZombiePlagueApi()
    {
        if (_zpApi is null)
        {
            return;
        }

        _zpApi.HZP_OnGameStart -= OnZombieGameStart;
        _zpApi.HZP_OnPlayerInfect -= OnZombiePlayerInfect;
        _zpApi.HZP_OnMotherZombieSelected -= OnZombieRoleSelected;
        _zpApi.HZP_OnNemesisSelected -= OnZombieRoleSelected;
        _zpApi.HZP_OnAssassinSelected -= OnZombieRoleSelected;
        _zpApi.HZP_OnHeroSelected -= OnZombieRoleSelected;
        _zpApi.HZP_OnSurvivorSelected -= OnZombieRoleSelected;
        _zpApi.HZP_OnSniperSelected -= OnZombieRoleSelected;

        _zpApi = null;
    }

    private void OnZombieGameStart(bool gameStart)
    {
        ApplyVisionForAllPlayersAfterDelay(0.2f, gameStart ? "zp-game-start" : "zp-game-end");
    }

    private void OnZombiePlayerInfect(IPlayer attacker, IPlayer infectedPlayer, bool grenade, string zombieClassName)
    {
        if (infectedPlayer is null || !infectedPlayer.IsValid)
        {
            return;
        }

        ApplyVisionForPlayerAfterDelay(infectedPlayer.PlayerID, 0.1f, "zp-infect");
    }

    private void OnZombieRoleSelected(IPlayer player)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        ApplyVisionForPlayerAfterDelay(player.PlayerID, 0.1f, "zp-role-selected");
    }

    private string T(ICommandContext context, string key, params object[] args)
    {
        if (context.Sender is IPlayer player && player.IsValid)
        {
            return Core.Translation.GetPlayerLocalizer(player)[key, args];
        }

        var template = Core.Localizer[key];
        if (string.IsNullOrWhiteSpace(template)
            || string.Equals(template, key, StringComparison.Ordinal))
        {
            template = key;
        }

        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
