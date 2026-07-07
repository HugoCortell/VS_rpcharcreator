using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangRpCharCreator;

public sealed class YangRpCharCreatorSystem : ModSystem
{
	private const string AssetDomain = "yangrpcharactercreator";
	private const string ConfigFile = "yangrpcharactercreator.json";
	private const string ImageSampleAssetPath = "config/imagesamples/";
	private const bool DebugOverwrite = false;										// DEBUG OVERWRITE. DON'T FORGET TO MARK FALSE BEFORE RELEASE.
	public const bool VerboseLogging = false;
	private const string NetworkChannelName = "yangrpcharcreator";
	private const string VanillaCharacterCreatorKey = "createCharacter";
	private const string CompletedKey = "yangrpcharcreator:completed";
	private const string SkipCharacterSelectionEvent = "skipcharacterselection";

	private ICoreServerAPI? ServerAPI;
	private ICoreClientAPI? ClientAPI;
	private IServerNetworkChannel? ServerChannel;
	private IClientNetworkChannel? ClientChannel;
	private GuiDialogRpCharacterCreator? Dialog;
	private GuiDialog? VisualDialog;
	private CharacterSystem? ClientCharacterSystem;
	private PlayerModelLibInterop? PlayerModelLib;
	private RpCharacterCreatorPacket? CreatorPacket;
	private readonly Dictionary<string, RpCharacterCreatorChoicePacket> PendingVisualChoices = new Dictionary<string, RpCharacterCreatorChoicePacket>();
	private bool VanillaSkippedCharacterSelection;
	private bool OpenedPlaceholderThisSession;
	private bool DidApplyDebugOverwrite;
	private bool PatchedCharacterTraitsTab;
	private int CharacterTraitsTabPatchAttempts;
	private string ImageDataPath = "";

	// CharacterSystem uses the default execute order of 0.1 | We need to mark vanilla character creation as complete before it checks PlayerJoin.
	public override double ExecuteOrder() => 0.05;

	public override void Start(ICoreAPI api)
	{
		api.Network.RegisterChannel(NetworkChannelName)
			.RegisterMessageType<RpCharacterCreatorPacket>()
			.RegisterMessageType<RpCharacterCreatorChoicePacket>()
			.RegisterMessageType<RpCharacterCreatorVisualCompletePacket>();
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		ServerAPI = api;
		ServerChannel = api.Network.GetChannel(NetworkChannelName);
		ServerChannel.SetMessageHandler<RpCharacterCreatorChoicePacket>(OnClientChoice);
		ServerChannel.SetMessageHandler<RpCharacterCreatorVisualCompletePacket>(OnClientVisualComplete);
		ImageDataPath = GetOrCreateModDataPath(api);
		PlayerModelLib = new PlayerModelLibInterop(api);

		api.Event.PlayerJoin += OnServerPlayerJoin;
		api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, LoadCreatorConfig);

		api.ChatCommands.Create("yangrpcharcreator")
			.RequiresPrivilege(Privilege.controlserver)
			.WithDescription("Force open the RP character creator for an online player.")
			.WithArgs(api.ChatCommands.Parsers.Word("playername"), api.ChatCommands.Parsers.OptionalWord("pageid"))
			.HandleWith(OnCmdOpenCreator);
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		ClientAPI = api;
		ClientChannel = api.Network.GetChannel(NetworkChannelName);
		ClientCharacterSystem = api.ModLoader.GetModSystem<CharacterSystem>();
		ClientChannel.SetMessageHandler<RpCharacterCreatorPacket>(OnCreatorPacket);

		api.Event.RegisterEventBusListener(OnClientEventBus, filterByEventName: SkipCharacterSelectionEvent);
		api.Event.RegisterCallback(_ => TryPatchCharacterTraitsTab(), 100);
	}

	private void OnServerPlayerJoin(IServerPlayer player)
	{
		// Vanilla CharacterSystem reads this same key to decide whether to open GuiDialogCreateCharacter.
		// Setting it to true suppresses the automatic first-join character creator.
		player.SetModData(VanillaCharacterCreatorKey, true);
		if (DebugOverwrite) { player.RemoveModdata(CompletedKey); }

		if (VerboseLogging && HasCompletedCreator(player))
		{
			ServerAPI?.Logger.Notification("[yangrpcharcreator] Suppressed vanilla character creator for {0}; RP creator already completed.", player.PlayerName);
			return;
		}

		EnterCreatorSpectatorMode(player, broadcast: false);
		SendCharacterCreation(player);
		ServerAPI?.Logger.Notification("[yangrpcharcreator] Suppressed vanilla character creator for {0}.", player.PlayerName);
	}

	public bool StartCharacterCreation(IServerPlayer player, string startPageId = "")
	{
		if (player == null) return false;

		player.SetModData(VanillaCharacterCreatorKey, true);
		player.RemoveModdata(CompletedKey);
		EnterCreatorSpectatorMode(player, broadcast: true);

		return SendCharacterCreation(player, startPageId);
	}

	private bool SendCharacterCreation(IServerPlayer player, string startPageId = "")
	{
		if (ServerAPI == null || ServerChannel == null) return false;
		if (CreatorPacket == null) { LoadCreatorConfig(); }

		RpCharacterCreatorPacket packet = CreatePacketForStartPage(startPageId);
		ServerChannel.SendPacket(packet, player);

		return true;
	}

	private RpCharacterCreatorPacket CreatePacketForStartPage(string startPageId)
	{
		RpCharacterCreatorPacket source = CreatorPacket ?? RpCharacterCreatorPacket.Empty;
		string requestedStart = (startPageId ?? "").Trim();

		return new RpCharacterCreatorPacket
		{
			StartPageId = requestedStart.Length > 0 ? requestedStart : source.StartPageId,
			Pages = source.Pages ?? Array.Empty<RpCharacterCreatorPage>()
		};
	}

	private TextCommandResult OnCmdOpenCreator(TextCommandCallingArgs args)
	{
		string playerName = args[0] as string ?? "";
		string startPageId = args[1] as string ?? "";

		IServerPlayer? player = GetOnlinePlayerByName(playerName);
		if (player == null) { return TextCommandResult.Error($"Can't open RP character creator: Unknown name '{playerName}', or player is offline."); }

		StartCharacterCreation(player, startPageId);

		string suffix = string.IsNullOrWhiteSpace(startPageId) ? "" : $" at page '{startPageId}'";
		return TextCommandResult.Success($"Opened RP character creator for {player.PlayerName}{suffix}.");
	}

	private IServerPlayer? GetOnlinePlayerByName(string playerName)
	{
		if (ServerAPI == null) return null;

		IPlayer[] players = ServerAPI.World.AllOnlinePlayers;
		for (int i = 0; i < players.Length; i++)
		{
			if (players[i] is IServerPlayer player && string.Equals(player.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)) { return player; }
		}

		return null;
	}


	private void EnterCreatorSpectatorMode(IServerPlayer player, bool broadcast)
	{
		if (ServerAPI == null) return;

		player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
		player.WorldData.FreeMove = true;
		player.WorldData.NoClip = true;
		player.WorldData.MoveSpeedMultiplier = 1.0f;

		// During PlayerJoin, vanilla will send the initial player data after all PlayerJoin handlers.
		// Sending our own player-data packet there is too early and can crash HudMouseTools on the client.
		if (broadcast && player.ConnectionState == EnumClientState.Playing) { player.BroadcastPlayerData(sendInventory: false); }

		if (VerboseLogging)	{ ServerAPI.Logger.Notification("[yangrpcharcreator] Set {0} to spectator mode for RP character creation.", player.PlayerName); }
	}

	private void ExitCreatorSpectatorMode(IServerPlayer player)
	{
		if (ServerAPI == null) return;

		player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		player.WorldData.FreeMove = false;
		player.WorldData.NoClip = false;
		player.WorldData.MoveSpeedMultiplier = 1.0f;

		if (player.ConnectionState == EnumClientState.Playing) { player.BroadcastPlayerData(sendInventory: false); }

		if (VerboseLogging)	{ ServerAPI.Logger.Notification("[yangrpcharcreator] Set {0} to survival mode after RP character creation.", player.PlayerName); }
	}

	private void OnClientChoice(IServerPlayer fromPlayer, RpCharacterCreatorChoicePacket packet)
	{
		if (HasCompletedCreator(fromPlayer)) return;
		if (CreatorPacket == null) { LoadCreatorConfig(); }
		if (CreatorPacket == null || CreatorPacket.Pages.Length == 0) return;

		if (!TryGetPage(packet.PageId, out RpCharacterCreatorPage page, out int pageIndex))
		{
			ServerAPI?.Logger.Warning("[yangrpcharcreator] Ignoring choice from {0}: unknown page '{1}'.", fromPlayer.PlayerName, packet.PageId);
			return;
		}

		RpCharacterCreatorChoice? choice = null;
		if (packet.ChoiceIndex >= 0)
		{
			if (packet.ChoiceIndex >= page.Choices.Length)
			{
				ServerAPI?.Logger.Warning
				(
					"[yangrpcharcreator] Ignoring choice from {0}: page '{1}' has no choice index {2}.",
					fromPlayer.PlayerName, page.Id, packet.ChoiceIndex
				);
				return;
			}

			choice = page.Choices[packet.ChoiceIndex];

			if (choice.CharacterCreate)
			{
				if (!string.IsNullOrWhiteSpace(choice.SetPlayerLib)) { ApplyPlayerModelLibReward(fromPlayer, choice.SetPlayerLib.Trim()); }

				PendingVisualChoices[fromPlayer.PlayerUID] = packet;
				return;
			}

			ApplyChoiceRewards(fromPlayer, choice);
		}

		if (ShouldCompleteCreator(pageIndex, choice)) { CompleteCreator(fromPlayer); }
	}

	private void OnClientVisualComplete(IServerPlayer fromPlayer, RpCharacterCreatorVisualCompletePacket packet)
	{
		if (HasCompletedCreator(fromPlayer)) return;

		if (!PendingVisualChoices.TryGetValue(fromPlayer.PlayerUID, out RpCharacterCreatorChoicePacket pendingChoice))
		{
			ServerAPI?.Logger.Warning("[yangrpcharcreator] Ignoring visual character completion from {0}: no pending visual choice.", fromPlayer.PlayerName);
			return;
		}

		PendingVisualChoices.Remove(fromPlayer.PlayerUID);

		if (CreatorPacket == null) { LoadCreatorConfig(); }
		if (CreatorPacket == null || CreatorPacket.Pages.Length == 0) return;

		if (!TryGetPage(pendingChoice.PageId, out RpCharacterCreatorPage page, out int pageIndex)) return;
		if (pendingChoice.ChoiceIndex < 0 || pendingChoice.ChoiceIndex >= page.Choices.Length) return;

		RpCharacterCreatorChoice choice = page.Choices[pendingChoice.ChoiceIndex];
		ApplyVisualCharacterSelection(fromPlayer, packet);
		ApplyChoiceRewards(fromPlayer, choice);

		if (choice.CharacterCreateCompletes || ShouldCompleteCreator(pageIndex, choice))
		{
			CompleteCreator(fromPlayer);
			return;
		}

		string nextPageId = GetNextPageId(pageIndex, choice);
		if (nextPageId.Length == 0)
		{
			CompleteCreator(fromPlayer);
			return;
		}

		SendCharacterCreation(fromPlayer, nextPageId);
	}

	private void CompleteCreator(IServerPlayer player)
	{
		MarkCreatorCompleted(player);
		ExitCreatorSpectatorMode(player);
		ServerAPI?.Logger.Notification("[yangrpcharcreator] {0} completed RP character creation!", player.PlayerName);
	}

	private void ApplyVisualCharacterSelection(IServerPlayer player, RpCharacterCreatorVisualCompletePacket packet)
	{
		if (ServerAPI == null || player.Entity == null || !packet.Completed) return;

		if (PlayerModelLib?.TryApplyVisualSelection(player, packet) == true) return;

		EntityBehaviorExtraSkinnable? behavior = player.Entity.GetBehavior<EntityBehaviorExtraSkinnable>();
		if (behavior == null)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] Unable to apply visual character creator result for {0}: EntityBehaviorExtraSkinnable not found.",
				player.PlayerName
			);
			return;
		}

		if (!string.IsNullOrWhiteSpace(packet.VoiceType) && !string.IsNullOrWhiteSpace(packet.VoicePitch))
		{
			behavior.ApplyVoice(packet.VoiceType, packet.VoicePitch, testTalk: false);
		}

		RpSkinPartSelection[] skinParts = packet.SkinParts ?? Array.Empty<RpSkinPartSelection>();
		for (int i = 0; i < skinParts.Length; i++)
		{
			string partCode = (skinParts[i].PartCode ?? "").Trim();
			string variantCode = (skinParts[i].Code ?? "").Trim();

			if (partCode.Length == 0 || variantCode.Length == 0) continue;

			behavior.selectSkinPart(partCode, variantCode, retesselateShape: false);
		}

		player.Entity.WatchedAttributes.MarkPathDirty("skinConfig");

		if (player.ConnectionState == EnumClientState.Playing) { player.BroadcastPlayerData(sendInventory: true); }

		if (VerboseLogging) { ServerAPI.Logger.Notification("[yangrpcharcreator] Applied visual character creator result for {0}.", player.PlayerName); }
	}

	private bool TryGetPage(string pageId, out RpCharacterCreatorPage page, out int pageIndex)
	{
		RpCharacterCreatorPage[] pages = CreatorPacket?.Pages ?? Array.Empty<RpCharacterCreatorPage>();
		pageId = (pageId ?? "").Trim();

		for (int i = 0; i < pages.Length; i++)
		{
			if (string.Equals(pages[i].Id, pageId, StringComparison.OrdinalIgnoreCase))
			{
				page = pages[i];
				pageIndex = i;
				return true;
			}
		}

		page = new RpCharacterCreatorPage();
		pageIndex = -1;
		return false;
	}

	private bool ShouldCompleteCreator(int pageIndex, RpCharacterCreatorChoice? choice)
	{
		if (choice?.Completed == true) return true;

		RpCharacterCreatorPage[] pages = CreatorPacket?.Pages ?? Array.Empty<RpCharacterCreatorPage>();
		if (pages.Length == 0) return true;

		if (choice != null && !string.IsNullOrWhiteSpace(choice.Goto))
		{
			for (int i = 0; i < pages.Length; i++) { if (string.Equals(pages[i].Id, choice.Goto, StringComparison.OrdinalIgnoreCase)) { return false; } }
		}

		return pageIndex + 1 >= pages.Length;
	}

	private string GetNextPageId(int pageIndex, RpCharacterCreatorChoice choice)
	{
		RpCharacterCreatorPage[] pages = CreatorPacket?.Pages ?? Array.Empty<RpCharacterCreatorPage>();

		if (!string.IsNullOrWhiteSpace(choice.Goto))
		{
			for (int i = 0; i < pages.Length; i++) { if (string.Equals(pages[i].Id, choice.Goto, StringComparison.OrdinalIgnoreCase)) { return pages[i].Id; } }
		}

		int nextIndex = pageIndex + 1;
		return nextIndex >= 0 && nextIndex < pages.Length ? pages[nextIndex].Id : "";
	}

	private void ApplyChoiceRewards(IServerPlayer player, RpCharacterCreatorChoice choice)
	{
		if (ServerAPI == null) return;

		bool changedPlayerData = false;

		if (!string.IsNullOrWhiteSpace(choice.SetPlayerLib)) { changedPlayerData |= ApplyPlayerModelLibReward(player, choice.SetPlayerLib.Trim()); }
		if (choice.TraitReward.Length > 0) { changedPlayerData |= ApplyTraitRewards(player, choice.TraitReward); }

		if (!string.IsNullOrWhiteSpace(choice.ClassReward)) { changedPlayerData |= ApplyClassReward(player, choice.ClassReward.Trim()); }
		else if (choice.TraitReward.Length > 0 || !string.IsNullOrWhiteSpace(choice.SetPlayerLib)) { ReapplyCurrentClass(player); }

		if (!string.IsNullOrWhiteSpace(choice.SetSpawn)) { ApplySetSpawn(player, choice.SetSpawn); }

		bool delayedStackRewards = false;
		if (!string.IsNullOrWhiteSpace(choice.SetLocation))
		{
			if (choice.StackReward.Length > 0)
			{
				delayedStackRewards = ApplySetLocation(player, choice.SetLocation, () =>
				{
					ApplyStackRewards(player, choice.StackReward);
					if (changedPlayerData)
					{
						player.Entity?.WatchedAttributes.MarkPathDirty("extraTraits");
						player.Entity?.WatchedAttributes.MarkPathDirty("characterClass");
					}
					player.BroadcastPlayerData(sendInventory: true);
				});
			}
			else { ApplySetLocation(player, choice.SetLocation); }
		}

		if (choice.StackReward.Length > 0 && !delayedStackRewards)
		{
			ApplyStackRewards(player, choice.StackReward);
			changedPlayerData = true;
		}

		if (changedPlayerData && !delayedStackRewards)
		{
			player.Entity?.WatchedAttributes.MarkPathDirty("extraTraits");
			player.Entity?.WatchedAttributes.MarkPathDirty("characterClass");
			player.BroadcastPlayerData(sendInventory: true);
		}
	}


	private bool ApplySetLocation(IServerPlayer player, string rawLocation, Action? afterTeleport = null)
	{
		if (ServerAPI == null || player.Entity == null) return false;

		if (!TryParsePosition(rawLocation, out Vec3d location))
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Could not parse setlocation '{0}' for {1}. Expected 'x y z'.", rawLocation, player.PlayerName);
			return false;
		}

		player.Entity.TeleportToDouble(location.X, location.Y, location.Z, afterTeleport);
		if (VerboseLogging) { ServerAPI.Logger.Notification("[yangrpcharcreator] Teleporting {0} to {1}.", player.PlayerName, rawLocation); }

		return true;
	}

	private bool ApplySetSpawn(IServerPlayer player, string rawSpawn)
	{
		if (ServerAPI == null) return false;

		if (!TryParsePosition(rawSpawn, out Vec3d spawn))
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Could not parse setspawn '{0}' for {1}. Expected 'x y z'.", rawSpawn, player.PlayerName);
			return false;
		}

		player.SetSpawnPosition(new PlayerSpawnPos((int)Math.Floor(spawn.X), (int)Math.Floor(spawn.Y), (int)Math.Floor(spawn.Z)));
		if (VerboseLogging) { ServerAPI.Logger.Notification("[yangrpcharcreator] Set respawn location for {0} to {1}.", player.PlayerName, rawSpawn); }

		return true;
	}

	private static bool TryParsePosition(string rawPosition, out Vec3d position)
	{
		position = new Vec3d();

		string[] parts = (rawPosition ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

		if (parts.Length < 3) return false;

		if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return false;
		if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
		if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return false;

		position.Set(x, y, z);
		return true;
	}

	private bool ApplyPlayerModelLibReward(IServerPlayer player, string modelCode)
	{
		if (PlayerModelLib == null) return false;

		return PlayerModelLib.TrySetPlayerModel(player, modelCode);
	}

	private bool ApplyTraitRewards(IServerPlayer player, string[] traitRewards)
	{
		if (player.Entity == null) return false;

		string[] existingTraits = player.Entity.WatchedAttributes.GetStringArray("extraTraits") ?? Array.Empty<string>();
		List<string> merged = new List<string>(existingTraits);
		HashSet<string> seen = new HashSet<string>(existingTraits, StringComparer.OrdinalIgnoreCase);
		bool changed = false;

		for (int i = 0; i < traitRewards.Length; i++)
		{
			string trait = (traitRewards[i] ?? "").Trim();
			if (trait.Length == 0 || !seen.Add(trait)) continue;

			merged.Add(trait);
			changed = true;
		}

		if (!changed) return false;

		player.Entity.WatchedAttributes.SetStringArray("extraTraits", merged.ToArray());
		if (VerboseLogging)
		{
			ServerAPI?.Logger.Notification
			(
				"[yangrpcharcreator] Added {0} extra trait reward{1} to {2}.",
				merged.Count - existingTraits.Length, merged.Count - existingTraits.Length == 1 ? "" : "s", player.PlayerName
			);
		}

		return true;
	}

	private bool ApplyClassReward(IServerPlayer player, string classCode)
	{
		bool applied = TrySetCharacterClass(player, classCode, "apply class reward");
		if (VerboseLogging && applied) { ServerAPI?.Logger.Notification("[yangrpcharcreator] Applied class reward '{0}' to {1}.", classCode, player.PlayerName); }

		return applied;
	}

	private void ReapplyCurrentClass(IServerPlayer player)
	{
		if (player.Entity == null || ServerAPI == null) return;

		string currentClass = player.Entity.WatchedAttributes.GetString("characterClass");
		if (string.IsNullOrWhiteSpace(currentClass)) return;

		TrySetCharacterClass(player, currentClass, "reapply current class after trait reward");
	}

	private bool TrySetCharacterClass(IServerPlayer player, string classCode, string reason)
	{
		if (player.Entity == null || ServerAPI == null) return false;

		ModSystem? characterSystem = ServerAPI.ModLoader.GetModSystem("Vintagestory.GameContent.CharacterSystem");
		if (characterSystem == null)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Unable to {0} '{1}' for {2}: CharacterSystem not found.", reason, classCode, player.PlayerName);
			return false;
		}

		System.Reflection.MethodInfo? setClassMethod = characterSystem.GetType().GetMethod("setCharacterClass");
		if (setClassMethod == null)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Unable to {0} '{1}' for {2}: setCharacterClass method not found.", reason, classCode, player.PlayerName);
			return false;
		}

		try
		{
			setClassMethod.Invoke(characterSystem, new object[] { player.Entity, classCode, false });
			return true;
		}
		catch (Exception ex)
		{
			ServerAPI.Logger.Warning
			(
				"[yangrpcharcreator] Unable to {0} '{1}' for {2}: {3}",
				reason, classCode, player.PlayerName, ex.InnerException?.Message ?? ex.Message
			);
			return false;
		}
	}

	private void ApplyStackRewards(IServerPlayer player, RpStackReward[] stackRewards)
	{
		if (ServerAPI == null) return;

		List<ItemStack> stacks = new List<ItemStack>(stackRewards.Length);

		for (int i = 0; i < stackRewards.Length; i++)
		{
			ItemStack? stack = ResolveStackReward(stackRewards[i], i);
			if (stack != null) { stacks.Add(stack); }
		}

		for (int i = 0; i < stacks.Count; i++) { if (IsBackpackStack(stacks[i])) { GiveStack(player, stacks[i]); } }
		for (int i = 0; i < stacks.Count; i++)
		{
			ItemStack stack = stacks[i];
			if (IsBackpackStack(stack)) continue;

			if (TryEquipWearable(player, stack) && stack.StackSize <= 0) continue;
			GiveStack(player, stack);
		}
	}

	private ItemStack? ResolveStackReward(RpStackReward reward, int index)
	{
		if (ServerAPI == null) return null;

		string type = (reward.Type ?? "").Trim().ToLowerInvariant();
		string rawCode = (reward.Code ?? "").Trim();
		int stackSize = reward.StackSize;

		if (rawCode.Length == 0)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring stackreward {0}: missing code.", index + 1);
			return null;
		}

		if (stackSize <= 0)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring stackreward '{0}': stacksize must be at least 1.", rawCode);
			return null;
		}

		AssetLocation code = new AssetLocation(rawCode);

		if (type == "item")
		{
			Item item = ServerAPI.World.GetItem(code);
			if (item != null && !item.IsMissing) { return new ItemStack(item, stackSize); }

			ServerAPI.Logger.Warning("[yangrpcharcreator] Could not resolve item stackreward '{0}'.", rawCode);
			return null;
		}

		if (type == "block")
		{
			Block block = ServerAPI.World.GetBlock(code);
			if (block != null && !block.IsMissing) { return new ItemStack(block, stackSize); }

			ServerAPI.Logger.Warning("[yangrpcharcreator] Could not resolve block stackreward '{0}'.", rawCode);
			return null;
		}

		ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring stackreward '{0}': type must be 'item' or 'block'.", rawCode);
		return null;
	}

	private static bool IsBackpackStack(ItemStack stack) { return (stack.Collectible.GetStorageFlags(stack) & EnumItemStorageFlags.Backpack) != 0; }

	private bool TryEquipWearable(IServerPlayer player, ItemStack stack)
	{
		if (stack.StackSize <= 0) return false;

		EnumCharacterDressType dressType = GetDressType(stack);
		if (dressType == EnumCharacterDressType.Unknown) return false;

		IInventory? characterInv = player.InventoryManager.GetOwnInventory("character");
		int slotIndex = (int)dressType;

		if (characterInv == null || slotIndex < 0 || slotIndex >= characterInv.Count) return false;

		ItemSlot slot = characterInv[slotIndex];
		if (!slot.Empty) return false;

		ItemStack equippedStack = stack.Clone();
		equippedStack.StackSize = 1;

		DummySlot sourceSlot = new DummySlot(equippedStack);
		if (!slot.CanHold(sourceSlot)) return false;

		slot.Itemstack = equippedStack;
		slot.MarkDirty();
		stack.StackSize--;

		if (VerboseLogging)
		{ 
			ServerAPI?.Logger.Notification
			(
				"[yangrpcharcreator] Equipped wearable reward '{0}' on {1} ({2}).",
				equippedStack.Collectible.Code, player.PlayerName, dressType
			);
		}

		return true;
	}

	private static EnumCharacterDressType GetDressType(ItemStack stack)
	{
		if (stack.Collectible.GetCollectibleInterface<IWearableStatsSupplier>() is IWearableStatsSupplier wearableStats)
		{
			return wearableStats.GetDressType(new DummySlot(stack));
		}

		JsonObject? attributes = stack.Collectible.Attributes;
		string? stackDressType = null;

		if (attributes != null)
		{
			stackDressType = attributes["clothescategory"].AsString(null);
			if (stackDressType == null) { stackDressType = attributes["attachableToEntity"]["categoryCode"].AsString(null); }
		}

		if (stackDressType != null && Enum.TryParse(stackDressType, ignoreCase: true, out EnumCharacterDressType dressType)) { return dressType; }

		return EnumCharacterDressType.Unknown;
	}

	private void GiveStack(IServerPlayer player, ItemStack stack)
	{
		if (ServerAPI == null || stack.StackSize <= 0) return;

		player.InventoryManager.TryGiveItemstack(stack, slotNotifyEffect: true);

		if (stack.StackSize > 0 && player.Entity?.Pos != null) { ServerAPI.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ); }
	}

	private void OnCreatorPacket(RpCharacterCreatorPacket packet)
	{
		CreatorPacket = packet ?? RpCharacterCreatorPacket.Empty;
		OpenedPlaceholderThisSession = false;

		if (Dialog != null) { Dialog.SetPages(CreatorPacket); }
		TryOpenCreatorDialog();
	}

	private void OnClientEventBus(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (eventName != SkipCharacterSelectionEvent) return;

		VanillaSkippedCharacterSelection = true;
		TryOpenCreatorDialog();
	}

	private void TryOpenCreatorDialog()
	{
		if (OpenedPlaceholderThisSession || !VanillaSkippedCharacterSelection || CreatorPacket == null || ClientAPI == null) return;

		ClientAPI.Event.EnqueueMainThreadTask(OpenCreatorDialog, "yangrpcharcreator-open");
	}

	private void OpenCreatorDialog()
	{
		if (OpenedPlaceholderThisSession || CreatorPacket == null || ClientAPI == null || ClientChannel == null) return;

		Dialog ??= new GuiDialogRpCharacterCreator(ClientAPI);
		Dialog.SetChoiceSelectedHandler(SendChoiceToServer);
		Dialog.SetPages(CreatorPacket);
		OpenedPlaceholderThisSession = true;
		Dialog.TryOpen();
	}

	private void SendChoiceToServer(string pageId, int choiceIndex, RpCharacterCreatorChoice choice)
	{
		ClientChannel?.SendPacket(new RpCharacterCreatorChoicePacket
		{
			PageId = pageId,
			ChoiceIndex = choiceIndex
		});

		if (choice.CharacterCreate) { OpenVisualCharacterCreator(choice); }
	}

	private void OpenVisualCharacterCreator(RpCharacterCreatorChoice choice)
	{
		if (ClientAPI == null || ClientCharacterSystem == null)
		{
			ClientAPI?.Logger.Warning("[yangrpcharcreator] Unable to open visual character creator: vanilla CharacterSystem not found.");
			return;
		}

		VisualDialog?.Dispose();

		if (PlayerModelLibVisualInterop.IsLoaded(ClientAPI))
		{
			if (PlayerModelLibVisualInterop.TryOpen(ClientAPI, ClientCharacterSystem, choice.SetPlayerLib, OnVisualCharacterCreatorCompleted, out GuiDialog? playerModelLibDialog))
			{
				VisualDialog = playerModelLibDialog;
				return;
			}

			if (string.IsNullOrWhiteSpace(choice.SetPlayerLib))
			{
				ClientAPI.Logger.Warning
				(
					"[yangrpcharcreator] PlayerModelLib is loaded, but its visual character creator could not be opened. " +
					"Continuing without visual customization to avoid the incompatible vanilla character creator."
				);
			}
			else
			{
				ClientAPI.Logger.Warning
				(
					"[yangrpcharcreator] PlayerModelLib visual character creator could not be opened for setplayerlib '{0}'. Continuing without visual customization.",
					choice.SetPlayerLib
				);
			}

			OnVisualCharacterCreatorCompleted(new RpCharacterCreatorVisualCompletePacket { Completed = true });
			return;
		}

		VisualDialog = new GuiDialogRpVisualCharacterCreator(ClientAPI, ClientCharacterSystem, OnVisualCharacterCreatorCompleted);
		VisualDialog.TryOpen();
	}

	private void OnVisualCharacterCreatorCompleted(RpCharacterCreatorVisualCompletePacket packet) { ClientChannel?.SendPacket(packet); }

	private void TryPatchCharacterTraitsTab()
	{
		if (PatchedCharacterTraitsTab || ClientAPI == null) return;

		ClientCharacterSystem ??= ClientAPI.ModLoader.GetModSystem<CharacterSystem>();

		GuiDialogCharacterBase? characterDialog = null;
		foreach (GuiDialog dialog in ClientAPI.Gui.LoadedGuis)
		{
			if (dialog is GuiDialogCharacterBase dialogCharacterBase)
			{
				characterDialog = dialogCharacterBase;
				break;
			}
		}

		if (characterDialog == null)
		{
			RetryPatchCharacterTraitsTab("vanilla character dialog not found yet");
			return;
		}

		int traitsTabIndex = -1;
		for (int i = 0; i < characterDialog.Tabs.Count; i++)
		{
			if (characterDialog.Tabs[i].DataInt == 1)
			{
				traitsTabIndex = i;
				break;
			}
		}

		if (traitsTabIndex < 0 || traitsTabIndex >= characterDialog.RenderTabHandlers.Count)
		{
			RetryPatchCharacterTraitsTab("vanilla traits tab not found yet");
			return;
		}

		characterDialog.RenderTabHandlers[traitsTabIndex] = ComposeRpAwareTraitsTab;
		PatchedCharacterTraitsTab = true;
		if (VerboseLogging) {  ClientAPI.Logger.Notification("[yangrpcharcreator] Patched the vanilla character traits tab to show RP creator extra traits."); }
	}

	private void RetryPatchCharacterTraitsTab(string reason)
	{
		if (PatchedCharacterTraitsTab || ClientAPI == null) return;

		CharacterTraitsTabPatchAttempts++;
		if (CharacterTraitsTabPatchAttempts >= 40)
		{
			ClientAPI.Logger.Warning("[yangrpcharcreator] Could not patch the vanilla character traits tab: {0}.", reason);
			return;
		}

		ClientAPI.Event.RegisterCallback(_ => TryPatchCharacterTraitsTab(), 250);
	}

	private void ComposeRpAwareTraitsTab(GuiComposer composer)
	{
		composer.AddRichtext
		(
			BuildRpAwareTraitText(),
			CairoFont.WhiteDetailText().WithLineHeightMultiplier(1.15),
			ElementBounds.Fixed(0.0, 25.0, 385.0, 280.0)
		);
	}

	private string BuildRpAwareTraitText()
	{
		if (ClientAPI == null || ClientCharacterSystem == null) return "";

		StringBuilder fullDesc = new StringBuilder();
		string characterClassCode = ClientAPI.World.Player.Entity.WatchedAttributes.GetString("characterClass");
		CharacterClass? characterClass = ResolveCharacterClass(characterClassCode);

		string[] classTraits = characterClass?.Traits ?? Array.Empty<string>();
		AppendTraitDescriptions(fullDesc, classTraits);

		if (characterClass != null && classTraits.Length == 0) { fullDesc.AppendLine(Lang.Get("No positive or negative traits")); } // Vanilla loc

		string[] extraTraits = ClientAPI.World.Player.Entity.WatchedAttributes.GetStringArray("extraTraits") ?? Array.Empty<string>();
		List<string> visibleExtraTraits = new List<string>();
		HashSet<string> seenClassTraits = BuildTraitSeenSet(classTraits);

		for (int i = 0; i < extraTraits.Length; i++)
		{
			string traitCode = (extraTraits[i] ?? "").Trim();
			if (traitCode.Length == 0) continue;

			if (seenClassTraits.Contains(traitCode)) continue;
			if (TryGetTrait(traitCode, out Trait? resolvedTrait) && seenClassTraits.Contains(resolvedTrait.Code)) continue;

			visibleExtraTraits.Add(traitCode);
		}

		if (visibleExtraTraits.Count > 0)
		{
			if (fullDesc.Length > 0) { fullDesc.AppendLine(); }
			fullDesc.AppendLine(Lang.Get("yangrpcharactercreator:additional-traits-title"));
			AppendTraitDescriptions(fullDesc, visibleExtraTraits.ToArray());
		}

		return fullDesc.ToString();
	}

	private CharacterClass? ResolveCharacterClass(string classCode)
	{
		if (ClientCharacterSystem == null) return null;

		classCode = (classCode ?? "").Trim();
		if (classCode.Length == 0) return null;

		if (ClientCharacterSystem.characterClassesByCode.TryGetValue(classCode, out CharacterClass characterClass)) { return characterClass; }

		for (int i = 0; i < ClientCharacterSystem.characterClasses.Count; i++)
		{
			if (string.Equals(ClientCharacterSystem.characterClasses[i].Code, classCode, StringComparison.OrdinalIgnoreCase))
			{
				return ClientCharacterSystem.characterClasses[i];
			}
		}

		return null;
	}

	private HashSet<string> BuildTraitSeenSet(string[] traitCodes)
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		for (int i = 0; i < traitCodes.Length; i++)
		{
			string traitCode = (traitCodes[i] ?? "").Trim();
			if (traitCode.Length == 0) continue;

			seen.Add(traitCode);
			if (TryGetTrait(traitCode, out Trait? trait)) { seen.Add(trait.Code); }
		}

		return seen;
	}

	private void AppendTraitDescriptions(StringBuilder fullDesc, string[] traitCodes)
	{
		List<Trait> traits = new List<Trait>();

		for (int i = 0; i < traitCodes.Length; i++) { if (TryGetTrait(traitCodes[i], out Trait? trait)) { traits.Add(trait); } }

		traits.Sort((a, b) => ((int)a.Type).CompareTo((int)b.Type));

		StringBuilder attributes = new StringBuilder();

		for (int i = 0; i < traits.Count; i++)
		{
			Trait trait = traits[i];
			attributes.Clear();

			if (trait.Attributes != null)
			{
				foreach (KeyValuePair<string, double> val in trait.Attributes)
				{
					if (attributes.Length > 0) { attributes.Append(", "); }
					attributes.Append(Lang.Get(string.Format(GlobalConstants.DefaultCultureInfo, "charattribute-{0}-{1}", val.Key, val.Value)));
				}
			}

			if (attributes.Length > 0)
			{
				fullDesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + trait.Code), attributes));
				continue;
			}

			string desc = Lang.GetIfExists("traitdesc-" + trait.Code);
			if (desc != null) { fullDesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + trait.Code), desc)); }
			else { fullDesc.AppendLine(Lang.Get("trait-" + trait.Code)); }
		}
	}

	private bool TryGetTrait(string traitCode, out Trait? trait)
	{
		trait = null;
		if (ClientCharacterSystem == null) return false;

		string rawCode = (traitCode ?? "").Trim();
		if (rawCode.Length == 0) return false;

		string shortCode = rawCode;
		int colonIndex = rawCode.IndexOf(':');
		if (colonIndex >= 0 && colonIndex + 1 < rawCode.Length) { shortCode = rawCode.Substring(colonIndex + 1); }

		if (ClientCharacterSystem.TraitsByCode.TryGetValue(rawCode, out trait)) return true;
		if (ClientCharacterSystem.TraitsByCode.TryGetValue(shortCode, out trait)) return true;

		for (int i = 0; i < ClientCharacterSystem.traits.Count; i++)
		{
			Trait candidate = ClientCharacterSystem.traits[i];
			if (string.Equals(candidate.Code, rawCode, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.Code, shortCode, StringComparison.OrdinalIgnoreCase))
			{
				trait = candidate;
				return true;
			}
		}

		return false;
	}

	private void LoadCreatorConfig()
	{
		if (ServerAPI == null) return;

		JToken? configToken;

		try
		{
			ApplyDebugOverwriteIfEnabled();
			CopyDefaultConfigToModConfigIfMissing();
			CopyImageSamplesToDataPathIfMissing();
			configToken = ServerAPI.LoadModConfig<JToken>(ConfigFile);
		}
		catch (Exception ex)
		{
			ServerAPI.Logger.Error("[yangrpcharcreator] Failed to load ModConfig/{0}.", ConfigFile);
			ServerAPI.Logger.Error(ex);
			CreatorPacket = CreateErrorPacket("Failed to load character creator config.");
			return;
		}

		JArray? pageArray = configToken as JArray;
		if (pageArray == null && configToken is JObject rootObj)
		{
			pageArray = rootObj["pages"] as JArray;
		}

		if (pageArray == null)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] ModConfig/{0} must contain a root array of pages, or an object with a pages array.", ConfigFile);
			CreatorPacket = CreateErrorPacket("Character creator config must contain a root array of pages.");
			return;
		}

		List<RpCharacterCreatorPage> pages = new List<RpCharacterCreatorPage>(pageArray.Count);
		HashSet<string> pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		for (int i = 0; i < pageArray.Count; i++)
		{
			if (ParsePage(pageArray[i], i, pageIds) is RpCharacterCreatorPage page) { pages.Add(page); }
		}

		if (pages.Count == 0)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] ModConfig/{0} contained no valid pages.", ConfigFile);
			CreatorPacket = CreateErrorPacket("Character creator config contained no valid pages.");
			return;
		}

		CreatorPacket = new RpCharacterCreatorPacket
		{
			StartPageId = pages[0].Id,
			Pages = pages.ToArray()
		};

		ServerAPI.Logger.Notification("[yangrpcharcreator] Loaded {0} character creator page{1} from ModConfig/{2}.", pages.Count, pages.Count == 1 ? "" : "s", ConfigFile);
	}

	private static string GetOrCreateModDataPath(ICoreAPI api)
	{
		string modDataRoot = api.GetOrCreateDataPath("ModData");
		string modDataPath = Path.Combine(modDataRoot, AssetDomain);
		Directory.CreateDirectory(modDataPath);

		return modDataPath;
	}

	private void ApplyDebugOverwriteIfEnabled()
	{
		if (!DebugOverwrite || DidApplyDebugOverwrite || ServerAPI == null) return;

		DidApplyDebugOverwrite = true;

		string modConfigPath = Path.Combine(ServerAPI.GetOrCreateDataPath("ModConfig"), ConfigFile);
		if (File.Exists(modConfigPath))
		{
			File.Delete(modConfigPath);
			ServerAPI.Logger.Notification("[yangrpcharcreator] DebugOverwrite deleted ModConfig/{0}.", ConfigFile);
		}

		if (Directory.Exists(ImageDataPath))
		{
			Directory.Delete(ImageDataPath, recursive: true);
			ServerAPI.Logger.Notification("[yangrpcharcreator] DebugOverwrite deleted ModData/{0}.", AssetDomain);
		}

		Directory.CreateDirectory(ImageDataPath);
	}

	private void CopyDefaultConfigToModConfigIfMissing()
	{
		if (ServerAPI == null) return;

		string modConfigPath = Path.Combine(ServerAPI.GetOrCreateDataPath("ModConfig"), ConfigFile);
		if (File.Exists(modConfigPath)) return;

		IAsset template = ServerAPI.Assets.Get(new AssetLocation(AssetDomain, "config/" + ConfigFile));
		File.WriteAllBytes(modConfigPath, template.Data);

		ServerAPI.Logger.Notification("[yangrpcharcreator] Copied default config template to ModConfig/{0}.", ConfigFile);
	}

	private void CopyImageSamplesToDataPathIfMissing()
	{
		if (ServerAPI == null) return;

		Directory.CreateDirectory(ImageDataPath);

		List<IAsset> sampleAssets = ServerAPI.Assets.GetMany(ImageSampleAssetPath, AssetDomain);
		int copied = 0;

		for (int i = 0; i < sampleAssets.Count; i++)
		{
			IAsset asset = sampleAssets[i];
			string assetPath = asset.Location.Path;

			if (!assetPath.StartsWith(ImageSampleAssetPath, StringComparison.OrdinalIgnoreCase)) continue;

			string relativePath = assetPath.Substring(ImageSampleAssetPath.Length);
			if (relativePath.Length == 0) continue;

			string targetPath = Path.Combine(ImageDataPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
			string? targetDir = Path.GetDirectoryName(targetPath);
			if (!string.IsNullOrEmpty(targetDir)) { Directory.CreateDirectory(targetDir); }

			if (File.Exists(targetPath)) continue;

			File.WriteAllBytes(targetPath, asset.Data);
			copied++;
		}

		if (copied > 0)
		{
			ServerAPI.Logger.Notification
			(
				"[yangrpcharcreator] Copied {0} sample image file{1} to {2}.",
				copied, copied == 1 ? "" : "s", ImageDataPath
			);
		}
	}

	private RpCharacterCreatorPage? ParsePage(JToken token, int pageIndex, HashSet<string> pageIds)
	{
		if (ServerAPI == null) return null;

		if (token is not JObject pageObj)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring page {0}: expected an object.", pageIndex + 1);
			return null;
		}

		string defaultId = (pageIndex + 1).ToString("D3");
		string id = (pageObj["id"]?.Value<string>() ?? "").Trim();
		if (id.Length == 0) { id = defaultId; }

		if (!pageIds.Add(id))
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Page id '{0}' is duplicated. Renaming duplicate to '{1}'.", id, defaultId);
			id = defaultId;
			pageIds.Add(id);
		}

		string desc = pageObj["desc"]?.Value<string>() ?? "";
		string image = (pageObj["image"]?.Value<string>() ?? "").Trim();

		List<RpCharacterCreatorChoice> choices = new List<RpCharacterCreatorChoice>();
		if (pageObj["choices"] is JArray choicesArray)
		{
			for (int i = 0; i < choicesArray.Count; i++)
			{
				if (ParseChoice(choicesArray[i], pageIndex, i) is RpCharacterCreatorChoice choice) { choices.Add(choice); }
			}
		}

		return new RpCharacterCreatorPage
		{
			Id = id,
			Image = image,
			ImageBytes = LoadImageBytes(image),
			Desc = desc,
			Choices = choices.ToArray()
		};
	}

	private byte[] LoadImageBytes(string image)
	{
		if (image.Length == 0) return Array.Empty<byte>();

		string imagePath = Path.Combine(ImageDataPath, image);
		byte[] bytes = File.ReadAllBytes(imagePath);

		ServerAPI?.Logger.Notification("[yangrpcharcreator] Loaded image '{0}' from ModData/{1} ({2} bytes).", image, AssetDomain, bytes.Length);
		return bytes;
	}

	private RpCharacterCreatorChoice? ParseChoice(JToken token, int pageIndex, int choiceIndex)
	{
		if (ServerAPI == null) return null;

		if (token is not JObject choiceObj)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring choice {0} on page {1}: expected an object.", choiceIndex + 1, pageIndex + 1);
			return null;
		}

		string title = (choiceObj["title"]?.Value<string>() ?? "").Trim();
		if (title.Length == 0)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring choice {0} on page {1}: missing title.", choiceIndex + 1, pageIndex + 1);
			return null;
		}

		JToken? characterCreateToken = choiceObj["charactercreate"];

		return new RpCharacterCreatorChoice
		{
			Title = title,
			Goto = (choiceObj["goto"]?.Value<string>() ?? "").Trim(),
			Completed = choiceObj["completed"]?.Value<bool>() ?? false,
			StackReward = ParseStackRewards(choiceObj["stackreward"], pageIndex, choiceIndex),
			TraitReward = ParseTraitRewards(choiceObj["traitreward"], pageIndex, choiceIndex),
			ClassReward = (choiceObj["classreward"]?.Value<string>() ?? "").Trim(),
			SetLocation = ParsePositionConfigValue(choiceObj["setlocation"]),
			SetSpawn = ParsePositionConfigValue(choiceObj["setspawn"]),
			SetPlayerLib = (choiceObj["setplayerlib"]?.Value<string>() ?? "").Trim(),
			CharacterCreate = characterCreateToken != null,
			CharacterCreateCompletes = characterCreateToken?.Value<bool>() ?? false,
			ShowRepercussions = choiceObj["showrepercussions"]?.Value<bool>() ?? true,
			CustomRepercussion = ParseCustomRepercussions
			(
				choiceObj["customreprercussion"] ?? choiceObj["customrepercussion"] ?? choiceObj["customrepercussions"],
				pageIndex,
				choiceIndex
			)
		};
	}

	private string[] ParseCustomRepercussions(JToken? token, int pageIndex, int choiceIndex)
	{
		if (ServerAPI == null || token == null) return Array.Empty<string>();

		if (token is not JArray array)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring customreprercussion on page {0}, choice {1}: expected an array.", pageIndex + 1, choiceIndex + 1);
			return Array.Empty<string>();
		}

		List<string> lines = new List<string>(array.Count);
		for (int i = 0; i < array.Count; i++)
		{
			string line = (array[i].Value<string>() ?? "").Trim();
			if (line.Length > 0) { lines.Add(line); }
		}

		return lines.ToArray();
	}

	private static string ParsePositionConfigValue(JToken? token)
	{
		if (token == null) return "";

		if (token is JArray array && array.Count >= 3) { return $"{array[0]} {array[1]} {array[2]}"; }

		return (token.Value<string>() ?? "").Trim();
	}

	private RpStackReward[] ParseStackRewards(JToken? token, int pageIndex, int choiceIndex)
	{
		if (ServerAPI == null || token == null) return Array.Empty<RpStackReward>();

		if (token is not JArray array)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring stackreward on page {0}, choice {1}: expected an array.", pageIndex + 1, choiceIndex + 1);
			return Array.Empty<RpStackReward>();
		}

		List<RpStackReward> rewards = new List<RpStackReward>(array.Count);
		for (int i = 0; i < array.Count; i++)
		{
			if (array[i] is not JObject obj)
			{
				ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring stackreward {0} on page {1}, choice {2}: expected an object.", i + 1, pageIndex + 1, choiceIndex + 1);
				continue;
			}

			string type = (obj["type"]?.Value<string>() ?? "").Trim();
			string code = (obj["code"]?.Value<string>() ?? "").Trim();
			int stackSize = obj["stacksize"]?.Value<int?>() ?? obj["size"]?.Value<int?>() ?? obj["quantity"]?.Value<int?>() ?? 1;

			rewards.Add(new RpStackReward
			{
				Type = type,
				Code = code,
				StackSize = stackSize
			});
		}

		return rewards.ToArray();
	}

	private string[] ParseTraitRewards(JToken? token, int pageIndex, int choiceIndex)
	{
		if (ServerAPI == null || token == null) return Array.Empty<string>();

		if (token is not JArray array)
		{
			ServerAPI.Logger.Warning("[yangrpcharcreator] Ignoring traitreward on page {0}, choice {1}: expected an array.", pageIndex + 1, choiceIndex + 1);
			return Array.Empty<string>();
		}

		List<string> rewards = new List<string>(array.Count);
		for (int i = 0; i < array.Count; i++)
		{
			string trait = (array[i].Value<string>() ?? "").Trim();
			if (trait.Length > 0) { rewards.Add(trait); }
		}

		return rewards.ToArray();
	}

	private static RpCharacterCreatorPacket CreateErrorPacket(string message)
	{
		return new RpCharacterCreatorPacket
		{
			StartPageId = "error",
			Pages = new[]
			{
				new RpCharacterCreatorPage
				{
					Id = "error",
					Desc = message,
					Choices = new[] { new RpCharacterCreatorChoice { Title = "Finish", Completed = true } }
				}
			}
		};
	}

	private static bool HasCompletedCreator(IServerPlayer player) { return player.GetModdata(CompletedKey) != null; }

	private static void MarkCreatorCompleted(IServerPlayer player) { player.SetModdata(CompletedKey, new byte[] { 1 }); }

	public override void Dispose()
	{
		if (ServerAPI != null) { ServerAPI.Event.PlayerJoin -= OnServerPlayerJoin; }
		if (ClientAPI != null) { ClientAPI.Event.UnregisterEventBusListener(OnClientEventBus); }

		Dialog?.Dispose();
		VisualDialog?.Dispose();
	}
}
