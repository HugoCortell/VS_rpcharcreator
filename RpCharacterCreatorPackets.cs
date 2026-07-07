using System;
using System.ComponentModel;
using ProtoBuf;

namespace YangRpCharCreator;

[ProtoContract]
public sealed class RpCharacterCreatorPacket
{
	public static readonly RpCharacterCreatorPacket Empty = new RpCharacterCreatorPacket();

	[ProtoMember(1)] public string StartPageId { get; set; } = "";
	[ProtoMember(2)] public RpCharacterCreatorPage[] Pages { get; set; } = Array.Empty<RpCharacterCreatorPage>();
}

[ProtoContract]
public sealed class RpCharacterCreatorPage
{
	[ProtoMember(1)] public string Id { get; set; } = "";
	[ProtoMember(2)] public string Image { get; set; } = "";
	[ProtoMember(3)] public string Desc { get; set; } = "";
	[ProtoMember(4)] public RpCharacterCreatorChoice[] Choices { get; set; } = Array.Empty<RpCharacterCreatorChoice>();
	[ProtoMember(5)] public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
}

[ProtoContract]
public sealed class RpCharacterCreatorChoice
{
	[ProtoMember(1)] 	public string Title { get; set; } = "";
	[ProtoMember(2)] 	public string Goto { get; set; } = "";
	[ProtoMember(3)] 	public bool Completed { get; set; }
	[ProtoMember(4)] 	public RpStackReward[] StackReward { get; set; } = Array.Empty<RpStackReward>();
	[ProtoMember(5)] 	public string[] TraitReward { get; set; } = Array.Empty<string>();
	[ProtoMember(6)] 	public string ClassReward { get; set; } = "";
	[ProtoMember(7)] 	public string SetLocation { get; set; } = "";
	[ProtoMember(8)] 	public string SetSpawn { get; set; } = "";
	[ProtoMember(9)] 	public bool CharacterCreate { get; set; }
	[ProtoMember(10)] 	public bool CharacterCreateCompletes { get; set; }
	[ProtoMember(11)] 	public string SetPlayerLib { get; set; } = "";
	[ProtoMember(12)] 	[DefaultValue(true)] public bool ShowRepercussions { get; set; } = true;
	[ProtoMember(13)] 	public string[] CustomRepercussion { get; set; } = Array.Empty<string>();
}

[ProtoContract]
public sealed class RpStackReward
{
	[ProtoMember(1)] public string Type { get; set; } = "";
	[ProtoMember(2)] public string Code { get; set; } = "";
	[ProtoMember(3)] public int StackSize { get; set; } = 1;
}

[ProtoContract]
public sealed class RpCharacterCreatorChoicePacket
{
	[ProtoMember(1)] public string PageId { get; set; } = "";
	[ProtoMember(2)] public int ChoiceIndex { get; set; }
}


[ProtoContract]
public sealed class RpCharacterCreatorVisualCompletePacket
{
	[ProtoMember(1)] public bool Completed { get; set; } = true;
	[ProtoMember(2)] public string VoiceType { get; set; } = "";
	[ProtoMember(3)] public string VoicePitch { get; set; } = "";
	[ProtoMember(4)] public RpSkinPartSelection[] SkinParts { get; set; } = Array.Empty<RpSkinPartSelection>();
}

[ProtoContract]
public sealed class RpSkinPartSelection
{
	[ProtoMember(1)] public string PartCode { get; set; } = "";
	[ProtoMember(2)] public string Code { get; set; } = "";
}
