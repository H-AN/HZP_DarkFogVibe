using System.Reflection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace HZP_DarkFog;

public sealed class HZP_DarkFog_Service
{
    private const string PostProcessDesignerName = "post_processing_volume";

    private readonly ISwiftlyCore _core;
    private readonly ILogger<HZP_DarkFog_Service> _logger;
    private readonly Dictionary<int, uint> _volumeEntityIndexByPlayerId = [];

    public HZP_DarkFog_Service(ISwiftlyCore core, ILogger<HZP_DarkFog_Service> logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool ApplyExposure(IPlayer? player, float exposure)
    {
        if (!TryGetExposureTarget(player, out var validPlayer))
        {
            return false;
        }

        exposure = MathF.Max(0.0f, exposure);

        _logger.LogInformation(
            "Applying exposure {Exposure} to player {PlayerId}.",
            exposure,
            validPlayer.PlayerID);

        RemovePlayerVolume(validPlayer.PlayerID);

        var volume = CreatePlayerVolume(validPlayer, exposure);
        if (volume is null || !volume.IsValid)
        {
            _logger.LogWarning(
                "Exposure apply aborted because the post-processing volume was not created for player {PlayerId}.",
                validPlayer.PlayerID);
            return false;
        }

        _volumeEntityIndexByPlayerId[validPlayer.PlayerID] = volume.Index;

        _logger.LogInformation(
            "Player {PlayerId} now owns post-processing volume {EntityIndex} with exposure {Exposure}.",
            validPlayer.PlayerID,
            volume.Index,
            exposure);

        RefreshPlayerVisuals(validPlayer);
        return true;
    }

    public void ResetPlayer(IPlayer? player)
    {
        if (player is null)
        {
            return;
        }

        _logger.LogInformation(
            "Resetting custom exposure for player {PlayerId}.",
            player.PlayerID);

        RemovePlayerVolume(player.PlayerID);

        if (player.IsValid && !player.IsFakeClient)
        {
            RefreshPlayerVisuals(player);
        }
    }

    public void RemovePlayer(int playerId)
    {
        _logger.LogInformation(
            "Removing dark fog state for player {PlayerId}.",
            playerId);
        RemovePlayerVolume(playerId);
    }

    public void ClearAllVolumes()
    {
        _logger.LogInformation(
            "Clearing all active post-processing volumes. Count={VolumeCount}.",
            _volumeEntityIndexByPlayerId.Count);

        foreach (var playerId in _volumeEntityIndexByPlayerId.Keys.ToArray())
        {
            RemovePlayerVolume(playerId);
        }
    }

    public void ClearAll()
    {
        _logger.LogInformation("Clearing all active dark fog state.");
        ClearAllVolumes();
    }

    private CPostProcessingVolume? CreatePlayerVolume(IPlayer player, float exposure)
    {
        CPostProcessingVolume? volume;

        try
        {
            volume = _core.EntitySystem.CreateEntityByDesignerName<CPostProcessingVolume>(PostProcessDesignerName);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(
                ex,
                "Failed to create post-processing volume for player {PlayerId} because designer '{DesignerName}' is invalid on this server.",
                player.PlayerID,
                PostProcessDesignerName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while creating post-processing volume for player {PlayerId}. Designer='{DesignerName}'.",
                player.PlayerID,
                PostProcessDesignerName);
            return null;
        }

        if (volume is null || !volume.IsValid)
        {
            _logger.LogWarning(
                "EntitySystem returned an invalid post-processing volume for player {PlayerId}. Designer='{DesignerName}'.",
                player.PlayerID,
                PostProcessDesignerName);
            return null;
        }

        volume.Master = true;
        volume.FadeDuration = 0.0f;
        volume.ExposureControl = true;
        volume.MinExposure = exposure;
        volume.MaxExposure = exposure;

        volume.DispatchSpawn();

        _logger.LogInformation(
            "Created post-processing volume {EntityIndex} for player {PlayerId}. Designer='{DesignerName}' Exposure={Exposure}.",
            volume.Index,
            player.PlayerID,
            PostProcessDesignerName,
            exposure);

        volume.SetTransmitState(false);
        _logger.LogInformation(
            "Configured volume {EntityIndex} to be hidden from all clients before owner assignment.",
            volume.Index);

        volume.SetTransmitState(true, player.PlayerID);
        _logger.LogInformation(
            "Configured volume {EntityIndex} to transmit only to player {PlayerId}.",
            volume.Index,
            player.PlayerID);

        return volume;
    }

    private void RemovePlayerVolume(int playerId)
    {
        if (!_volumeEntityIndexByPlayerId.Remove(playerId, out var entityIndex))
        {
            _logger.LogDebug(
                "No active post-processing volume was tracked for player {PlayerId}.",
                playerId);
            return;
        }

        try
        {
            var volume = _core.EntitySystem.GetEntityByIndex<CPostProcessingVolume>(entityIndex);
            if (volume is not null && volume.IsValid)
            {
                _logger.LogInformation(
                    "Despawning post-processing volume {EntityIndex} for player {PlayerId}.",
                    entityIndex,
                    playerId);
                volume.Despawn();
                return;
            }

            _logger.LogDebug(
                "Tracked post-processing volume {EntityIndex} for player {PlayerId} was already invalid during removal.",
                entityIndex,
                playerId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to despawn post-processing volume {EntityIndex} for player {PlayerId}.",
                entityIndex,
                playerId);
        }
    }

    private void RefreshPlayerVisuals(IPlayer player)
    {
        if (!TryGetExposureTarget(player, out var validPlayer))
        {
            return;
        }

        _logger.LogInformation(
            "Refreshing visuals for player {PlayerId}.",
            validPlayer.PlayerID);

        if (TryForceFullUpdate(validPlayer))
        {
            return;
        }

        try
        {
            var eyeAngles = validPlayer.PlayerPawn?.EyeAngles;
            validPlayer.Teleport(null, eyeAngles, null);
            _logger.LogInformation(
                "ForceFullUpdate was unavailable for player {PlayerId}; used Teleport refresh fallback.",
                validPlayer.PlayerID);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Fallback visual refresh failed for player {PlayerId}.",
                validPlayer.PlayerID);
        }
    }

    private bool TryForceFullUpdate(IPlayer player)
    {
        try
        {
            if (TryInvokeForceFullUpdate(player))
            {
                _logger.LogInformation(
                    "ForceFullUpdate succeeded directly on IPlayer for player {PlayerId}.",
                    player.PlayerID);
                return true;
            }

            if (TryInvokeForceFullUpdate(player.Controller))
            {
                _logger.LogInformation(
                    "ForceFullUpdate succeeded on Controller for player {PlayerId}.",
                    player.PlayerID);
                return true;
            }

            var engine = _core.Engine;
            foreach (var candidate in EnumeratePotentialClientObjects(engine, player))
            {
                if (TryInvokeForceFullUpdate(candidate))
                {
                    _logger.LogInformation(
                        "ForceFullUpdate succeeded through engine candidate {CandidateType} for player {PlayerId}.",
                        candidate?.GetType().FullName,
                        player.PlayerID);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "ForceFullUpdate reflection path failed for player {PlayerId}.",
                player.PlayerID);
        }

        _logger.LogDebug(
            "ForceFullUpdate could not be resolved for player {PlayerId}.",
            player.PlayerID);
        return false;
    }

    private static bool TryInvokeForceFullUpdate(object? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var method = candidate.GetType().GetMethod(
            "ForceFullUpdate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (method is null)
        {
            return false;
        }

        method.Invoke(candidate, null);
        return true;
    }

    private static IEnumerable<object?> EnumeratePotentialClientObjects(object engine, IPlayer player)
    {
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var property in engine.GetType().GetProperties(bindingFlags))
        {
            if (!property.CanRead)
            {
                continue;
            }

            if (!LooksLikeClientCarrier(property.Name, property.PropertyType))
            {
                continue;
            }

            object? value = null;
            try
            {
                value = property.GetValue(engine);
            }
            catch
            {
            }

            if (value is not null)
            {
                yield return value;
            }
        }

        foreach (var method in engine.GetType().GetMethods(bindingFlags))
        {
            if (!LooksLikeClientLookup(method.Name, method.ReturnType))
            {
                continue;
            }

            var args = BuildLookupArguments(method.GetParameters(), player);
            if (args is null)
            {
                continue;
            }

            object? value = null;
            try
            {
                value = method.Invoke(engine, args);
            }
            catch
            {
            }

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static bool LooksLikeClientCarrier(string name, Type propertyType)
    {
        return name.Contains("Client", StringComparison.OrdinalIgnoreCase)
            || propertyType.Name.Contains("Client", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeClientLookup(string name, Type returnType)
    {
        return (name.Contains("Client", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Player", StringComparison.OrdinalIgnoreCase))
            && returnType != typeof(void);
    }

    private static object[]? BuildLookupArguments(ParameterInfo[] parameters, IPlayer player)
    {
        if (parameters.Length == 0)
        {
            return [];
        }

        if (parameters.Length > 1)
        {
            return null;
        }

        var parameterType = parameters[0].ParameterType;

        if (parameterType == typeof(int))
        {
            return [player.PlayerID];
        }

        if (parameterType == typeof(uint))
        {
            return [(uint)Math.Max(player.PlayerID, 0)];
        }

        if (player.Controller is not null && parameterType.IsInstanceOfType(player.Controller))
        {
            return [player.Controller];
        }

        if (player.PlayerPawn is not null && parameterType.IsInstanceOfType(player.PlayerPawn))
        {
            return [player.PlayerPawn];
        }

        return null;
    }

    private static bool TryGetExposureTarget(IPlayer? player, out IPlayer validPlayer)
    {
        validPlayer = null!;

        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        validPlayer = player;
        return true;
    }
}
