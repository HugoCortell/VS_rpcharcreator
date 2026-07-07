using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace YangRpCharCreator;

internal sealed class PlayerModelLibInterop
{
	private const string ModId = "playermodellib";
	private const string CustomModelsSystemName = "PlayerModelLib.CustomModelsSystem";
	private const string PlayerSkinBehaviorName = "skinnableplayercustommodel";

	private readonly ICoreServerAPI ServerAPI;

	public PlayerModelLibInterop(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;
	}

	public bool TrySetPlayerModel(IServerPlayer player, string requestedModelCode)
	{
		requestedModelCode = (requestedModelCode ?? "").Trim();
		if (requestedModelCode.Length == 0) return false;

		string playliberror = "[yangrpcharcreator] Could not apply setplayerlib '{0}' to {1}: ";

		if (!ServerAPI.ModLoader.IsModEnabled(ModId))
		{
			ServerAPI.Logger.Warning(playliberror + "PlayerModelLib is not loaded.", requestedModelCode, player.PlayerName);
			return false;
		}

		ModSystem? customModelsSystem = ServerAPI.ModLoader.GetModSystem(CustomModelsSystemName);
		if (customModelsSystem == null)
		{
			ServerAPI.Logger.Warning(playliberror + "PlayerModelLib CustomModelsSystem was not found.", requestedModelCode, player.PlayerName);
			return false;
		}

		object? customModels = customModelsSystem.GetType().GetProperty("CustomModels")?.GetValue(customModelsSystem);
		if (customModels == null)
		{
			ServerAPI.Logger.Warning(playliberror + "PlayerModelLib model registry was not found.", requestedModelCode, player.PlayerName);
			return false;
		}

		if (!TryResolveModel(customModels, requestedModelCode, out string resolvedModelCode, out object modelData))
		{
			ServerAPI.Logger.Warning(playliberror + "no matching PlayerModelLib model exists.", requestedModelCode, player.PlayerName);
			return false;
		}

		EntityBehavior? playerSkinBehavior = player.Entity.GetBehavior(PlayerSkinBehaviorName);
		if (playerSkinBehavior == null)
		{
			ServerAPI.Logger.Warning(playliberror + "PlayerModelLib player skin behavior was not found on the entity.", requestedModelCode, player.PlayerName);
			return false;
		}

		string[] previousTraits = player.Entity.WatchedAttributes.GetStringArray("extraTraits") ?? Array.Empty<string>();
		string[] modelTraits = GetStringArrayProperty(modelData, "ExtraTraits");
		string[] mergedTraits = MergeTraits(previousTraits, modelTraits);

		player.Entity.WatchedAttributes.SetString("skinModel", resolvedModelCode);
		player.Entity.WatchedAttributes.SetStringArray("extraTraits", mergedTraits);
		player.Entity.WatchedAttributes.MarkPathDirty("skinModel");
		player.Entity.WatchedAttributes.MarkPathDirty("extraTraits");

		TryUpdateEntityProperties(player, playerSkinBehavior, resolvedModelCode);

		if (YangRpCharCreatorSystem.VerboseLogging)
		{  
			ServerAPI.Logger.Notification("[yangrpcharcreator] Applied PlayerModelLib model '{0}' to {1}.", resolvedModelCode, player.PlayerName);
		}
		return true;
	}

	public bool TryApplyVisualSelection(IServerPlayer player, RpCharacterCreatorVisualCompletePacket packet)
	{
		if (!packet.Completed || !ServerAPI.ModLoader.IsModEnabled(ModId)) return false;

		EntityBehavior? playerSkinBehavior = player.Entity.GetBehavior(PlayerSkinBehaviorName);
		if (playerSkinBehavior == null) return false;

		Type behaviorType = playerSkinBehavior.GetType();

		if (!string.IsNullOrWhiteSpace(packet.VoiceType) && !string.IsNullOrWhiteSpace(packet.VoicePitch))
		{
			MethodInfo? applyVoiceMethod = behaviorType.GetMethod("ApplyVoice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			try { applyVoiceMethod?.Invoke(playerSkinBehavior, new object[] { packet.VoiceType, packet.VoicePitch, false }); }
			catch (Exception ex)
			{ 
				ServerAPI.Logger.Warning("[yangrpcharcreator] PlayerModelLib ApplyVoice failed for {0}: {1}", player.PlayerName, ex.InnerException?.Message ?? ex.Message);
			}
		}

		MethodInfo? selectSkinPartMethod = behaviorType.GetMethod("SelectSkinPart", new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) });
		if (selectSkinPartMethod == null)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] PlayerModelLib visual selection was not applied for {0}: SelectSkinPart method not found.", player.PlayerName);
			return false;
		}

		RpSkinPartSelection[] skinParts = packet.SkinParts ?? Array.Empty<RpSkinPartSelection>();
		for (int i = 0; i < skinParts.Length; i++)
		{
			string partCode = (skinParts[i].PartCode ?? "").Trim();
			string code = (skinParts[i].Code ?? "").Trim();
			if (partCode.Length == 0 || code.Length == 0) continue;

			try { selectSkinPartMethod.Invoke(playerSkinBehavior, new object[] { partCode, code, false, false }); }
			catch (Exception ex)
			{
				ServerAPI.Logger.Warning
				(
					"[yangrpcharcreator] PlayerModelLib SelectSkinPart failed for {0} ({1}:{2}): {3}",
					player.PlayerName, partCode, code, ex.InnerException?.Message ?? ex.Message
				);
			}
		}

		player.Entity.WatchedAttributes.MarkPathDirty("skinConfig");
		return true;
	}

	private bool TryResolveModel(object customModels, string requestedModelCode, out string resolvedModelCode, out object modelData)
	{
		resolvedModelCode = "";
		modelData = new object();

		List<(string Code, object Data)> exactMatches = new List<(string Code, object Data)>();
		List<(string Code, object Data)> shortMatches = new List<(string Code, object Data)>();

		foreach (object? entry in (IEnumerable)customModels)
		{
			if (entry == null) continue;

			Type entryType = entry.GetType();
			string? key = entryType.GetProperty("Key")?.GetValue(entry) as string;
			object? value = entryType.GetProperty("Value")?.GetValue(entry);

			if (key == null || value == null) continue;

			if (string.Equals(key, requestedModelCode, StringComparison.OrdinalIgnoreCase))
			{
				exactMatches.Add((key, value));
				continue;
			}

			int domainSeparatorIndex = key.IndexOf(':');
			if (domainSeparatorIndex >= 0 && domainSeparatorIndex + 1 < key.Length)
			{
				string shortCode = key.Substring(domainSeparatorIndex + 1);
				if (string.Equals(shortCode, requestedModelCode, StringComparison.OrdinalIgnoreCase)) { shortMatches.Add((key, value)); }
			}
		}

		if (exactMatches.Count == 1)
		{
			resolvedModelCode = exactMatches[0].Code;
			modelData = exactMatches[0].Data;
			return true;
		}

		if (exactMatches.Count > 1)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] setplayerlib '{0}' matched multiple PlayerModelLib models exactly. Use the full domain:model code.",
				requestedModelCode
			);
			return false;
		}

		if (shortMatches.Count == 1)
		{
			resolvedModelCode = shortMatches[0].Code;
			modelData = shortMatches[0].Data;
			return true;
		}

		if (shortMatches.Count > 1)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] setplayerlib '{0}' matched multiple PlayerModelLib models by short code. Use the full domain:model code.",
				requestedModelCode
			);
			return false;
		}

		return false;
	}

	private static string[] GetStringArrayProperty(object source, string propertyName)
	{
		object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
		if (value is string[] stringArray) return stringArray;

		if (value is IEnumerable enumerable)
		{
			List<string> strings = new List<string>();

			foreach (object? item in enumerable) { if (item is string text) { strings.Add(text); } }

			return strings.ToArray();
		}

		return Array.Empty<string>();
	}

	internal static string[] MergeTraits(params string[][] traitGroups)
	{
		List<string> merged = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		for (int groupIndex = 0; groupIndex < traitGroups.Length; groupIndex++)
		{
			string[] traits = traitGroups[groupIndex] ?? Array.Empty<string>();

			for (int i = 0; i < traits.Length; i++)
			{
				string trait = (traits[i] ?? "").Trim();
				if (trait.Length == 0 || !seen.Add(trait)) continue;

				merged.Add(trait);
			}
		}

		return merged.ToArray();
	}

	private void TryUpdateEntityProperties(IServerPlayer player, EntityBehavior playerSkinBehavior, string resolvedModelCode)
	{
		MethodInfo? updateMethod = playerSkinBehavior.GetType().GetMethod("UpdateEntityProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (updateMethod == null)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] PlayerModelLib model '{0}' was set for {1}, but UpdateEntityProperties() was not found.",
				resolvedModelCode, player.PlayerName
			);
			return;
		}

		try { updateMethod.Invoke(playerSkinBehavior, Array.Empty<object>()); }
		catch (Exception ex)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] PlayerModelLib model '{0}' was set for {1}, but UpdateEntityProperties() failed: {2}",
				resolvedModelCode, player.PlayerName, ex.InnerException?.Message ?? ex.Message
			);
		}
	}
}

internal static class PlayerModelLibVisualInterop
{
	private const string ModId = "playermodellib";
	private const string CustomModelsSystemName = "PlayerModelLib.CustomModelsSystem";
	private const string CustomDialogTypeName = "PlayerModelLib.GuiDialogCreateCustomCharacter";
	private const string PlayerSkinBehaviorName = "skinnableplayercustommodel";

	public static bool IsLoaded(ICoreClientAPI capi) { return capi.ModLoader.IsModEnabled(ModId); }

	public static string ResolveModelDisplayName(ICoreClientAPI capi, string requestedModelCode)
	{
		requestedModelCode = (requestedModelCode ?? "").Trim();
		if (requestedModelCode.Length == 0 || !IsLoaded(capi)) return requestedModelCode;

		ModSystem? customModelsSystem = capi.ModLoader.GetModSystem(CustomModelsSystemName);
		object? customModels = customModelsSystem?.GetType().GetProperty("CustomModels")?.GetValue(customModelsSystem);
		if (customModels == null) return requestedModelCode;

		if (!TryResolveModel(customModels, requestedModelCode, out string resolvedModelCode, out object modelData)) { return requestedModelCode; }

		string modelName = GetStringProperty(modelData, "Name").Trim();
		if (modelName.Length > 0) return modelName;

		try
		{
			AssetLocation location = new AssetLocation(resolvedModelCode);
			string? translated = Lang.GetIfExists(location.Domain + ":playermodel-" + location.Path);
			if (!string.IsNullOrWhiteSpace(translated)) return translated;
		}
		catch { }

		return resolvedModelCode.Length > 0 ? resolvedModelCode : requestedModelCode;
	}

	public static bool TryOpen
	(
		ICoreClientAPI capi, CharacterSystem characterSystem, string requestedModelCode,
		Action<RpCharacterCreatorVisualCompletePacket> completed, out GuiDialog? dialog
	)
	{
		dialog = null;

		requestedModelCode = (requestedModelCode ?? "").Trim();

		if (!IsLoaded(capi)) return false;

		if (requestedModelCode.Length > 0 && !TryApplyLocalModel(capi, requestedModelCode)) return false;

		Type? dialogType = FindType(CustomDialogTypeName);
		if (dialogType == null)
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib custom character creator dialog type was not found.");
			return false;
		}

		object? instance;
		try
		{
			instance = Activator.CreateInstance(dialogType, capi, characterSystem);
		}
		catch (Exception ex)
		{
			capi.Logger.Warning("[yangrpcharcreator] Failed to create PlayerModelLib visual character creator: {0}", ex.InnerException?.Message ?? ex.Message);
			return false;
		}

		if (instance is not GuiDialog guiDialog)
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib visual character creator was not a GuiDialog.");
			return false;
		}

		ConfigureVisualOnlyTabs(instance);

		guiDialog.OnClosed += () =>
		{
			if (GetBoolProperty(instance, "DidSelect"))
			{
				completed(CreateVisualCompletePacket(capi));
				return;
			}

			capi.Logger.Notification("[yangrpcharcreator] PlayerModelLib visual character creator was closed before confirmation. Re-opening...");
			capi.Event.EnqueueMainThreadTask(() =>
			{
				TryOpen(capi, characterSystem, requestedModelCode, completed, out _);
			}, "yangrpcharcreator-reopen-playerlib-visual");
		};

		dialog = guiDialog;
		guiDialog.TryOpen();

		return true;
	}

	private static bool TryApplyLocalModel(ICoreClientAPI capi, string requestedModelCode)
	{
		ModSystem? customModelsSystem = capi.ModLoader.GetModSystem(CustomModelsSystemName);
		if (customModelsSystem == null)
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib CustomModelsSystem was not found on the client.");
			return false;
		}

		object? customModels = customModelsSystem.GetType().GetProperty("CustomModels")?.GetValue(customModelsSystem);
		if (customModels == null)
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib model registry was not found on the client.");
			return false;
		}

		if (!TryResolveModel(customModels, requestedModelCode, out string resolvedModelCode, out object modelData))
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib model '{0}' was not found on the client.", requestedModelCode);
			return false;
		}

		EntityBehavior? playerSkinBehavior = capi.World.Player.Entity.GetBehavior(PlayerSkinBehaviorName);
		if (playerSkinBehavior == null)
		{
			capi.Logger.Warning("[yangrpcharcreator] PlayerModelLib player skin behavior was not found on the client entity.");
			return false;
		}

		string[] previousTraits = capi.World.Player.Entity.WatchedAttributes.GetStringArray("extraTraits") ?? Array.Empty<string>();
		string[] modelTraits = GetStringArrayProperty(modelData, "ExtraTraits");
		string[] mergedTraits = PlayerModelLibInterop.MergeTraits(previousTraits, modelTraits);

		capi.World.Player.Entity.WatchedAttributes.SetString("skinModel", resolvedModelCode);
		capi.World.Player.Entity.WatchedAttributes.SetStringArray("extraTraits", mergedTraits);

		MethodInfo? updateMethod = playerSkinBehavior.GetType().GetMethod("UpdateEntityProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		try { updateMethod?.Invoke(playerSkinBehavior, Array.Empty<object>()); }
		catch (Exception ex)
		{
			capi.Logger.Warning
			(
				"[yangrpcharcreator] PlayerModelLib client UpdateEntityProperties failed for '{0}': {1}",
				resolvedModelCode, ex.InnerException?.Message ?? ex.Message
			);
		}

		return true;
	}

	private static void ConfigureVisualOnlyTabs(object dialog)
	{
		object? tabsEnabled = dialog.GetType().GetProperty("TabsEnabled")?.GetValue(dialog);
		if (tabsEnabled is not IDictionary dictionary) return;

		if (dictionary.Contains("model")) { dictionary["model"] = false; }
		if (dictionary.Contains("class")) { dictionary["class"] = false; }
		if (dictionary.Contains("skin")) { dictionary["skin"] = true; }

		PropertyInfo? currentTab = dialog.GetType().GetProperty("CurrentTab");
		if (currentTab?.CanWrite == true) { currentTab.SetValue(dialog, 0); }
	}

	private static RpCharacterCreatorVisualCompletePacket CreateVisualCompletePacket(ICoreClientAPI capi)
	{
		RpCharacterCreatorVisualCompletePacket packet = new RpCharacterCreatorVisualCompletePacket { Completed = true };

		EntityBehavior? playerSkinBehavior = capi.World.Player.Entity.GetBehavior(PlayerSkinBehaviorName);
		if (playerSkinBehavior == null) return packet;

		Type behaviorType = playerSkinBehavior.GetType();

		packet.VoiceType = GetStringProperty(playerSkinBehavior, "VoiceType");
		packet.VoicePitch = GetStringProperty(playerSkinBehavior, "VoicePitch");

		object? appliedSkinParts = behaviorType.GetProperty("AppliedSkinParts")?.GetValue(playerSkinBehavior);
		object? skinPartSource = appliedSkinParts?.GetType().GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(appliedSkinParts, Array.Empty<object>()) ?? appliedSkinParts;

		List<RpSkinPartSelection> skinParts = new List<RpSkinPartSelection>();
		if (skinPartSource is IEnumerable enumerable)
		{
			foreach (object? part in enumerable)
			{
				if (part == null) continue;

				string partCode = GetStringProperty(part, "PartCode");
				string code = GetStringProperty(part, "Code");
				if (partCode.Length == 0 || code.Length == 0) continue;

				skinParts.Add(new RpSkinPartSelection
				{
					PartCode = partCode,
					Code = code
				});
			}
		}

		packet.SkinParts = skinParts.ToArray();
		return packet;
	}

	private static bool TryResolveModel(object customModels, string requestedModelCode, out string resolvedModelCode, out object modelData)
	{
		resolvedModelCode = "";
		modelData = new object();

		List<(string Code, object Data)> exactMatches = new List<(string Code, object Data)>();
		List<(string Code, object Data)> shortMatches = new List<(string Code, object Data)>();

		foreach (object? entry in (IEnumerable)customModels)
		{
			if (entry == null) continue;

			Type entryType = entry.GetType();
			string? key = entryType.GetProperty("Key")?.GetValue(entry) as string;
			object? value = entryType.GetProperty("Value")?.GetValue(entry);

			if (key == null || value == null) continue;

			if (string.Equals(key, requestedModelCode, StringComparison.OrdinalIgnoreCase))
			{
				exactMatches.Add((key, value));
				continue;
			}

			int domainSeparatorIndex = key.IndexOf(':');
			if (domainSeparatorIndex >= 0 && domainSeparatorIndex + 1 < key.Length)
			{
				string shortCode = key.Substring(domainSeparatorIndex + 1);
				if (string.Equals(shortCode, requestedModelCode, StringComparison.OrdinalIgnoreCase)) { shortMatches.Add((key, value)); }
			}
		}

		if (exactMatches.Count == 1)
		{
			resolvedModelCode = exactMatches[0].Code;
			modelData = exactMatches[0].Data;
			return true;
		}

		if (shortMatches.Count == 1)
		{
			resolvedModelCode = shortMatches[0].Code;
			modelData = shortMatches[0].Data;
			return true;
		}

		return false;
	}

	private static string[] GetStringArrayProperty(object source, string propertyName)
	{
		object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
		if (value is string[] stringArray) return stringArray;

		if (value is IEnumerable enumerable)
		{
			List<string> strings = new List<string>();

			foreach (object? item in enumerable) { if (item is string text) { strings.Add(text); } }

			return strings.ToArray();
		}

		return Array.Empty<string>();
	}

	private static string GetStringProperty(object source, string propertyName) { return source.GetType().GetProperty(propertyName)?.GetValue(source) as string ?? ""; }

	private static bool GetBoolProperty(object source, string propertyName)
	{
		object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
		return value is bool boolValue && boolValue;
	}

	private static Type? FindType(string fullName)
	{
		Type? type = Type.GetType(fullName);
		if (type != null) return type;

		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			type = assembly.GetType(fullName);
			if (type != null) return type;
		}

		return null;
	}
}
