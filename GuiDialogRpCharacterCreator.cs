using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace YangRpCharCreator;

public sealed class GuiDialogRpCharacterCreator : GuiDialog
{
	private const double MinScreenWidth = 640.0;
	private const double MinScreenHeight = 420.0;
	private const double MinContentWidth = 430.0;
	private const double MaxContentWidth = 820.0;
	private const double MaxImageWidth = 460.0;
	private const double MinImageWidth = 260.0;
	private const double ContentPaddingX = 30.0;
	private const double ContentPaddingY = 28.0;
	private const double SectionGap = 22.0;
	private const double ScrollbarWidth = 16.0;
	private const double ScrollbarGap = 8.0;
	private const double ChoiceButtonPadX = 10.0;
	private const double ChoiceButtonPadY = 4.0;
	private const double ChoiceButtonInnerHeight = 38.0;
	private const double ChoiceButtonDoubleInnerHeight = 74.0;
	private const double ChoiceGap = 10.0;
	private const double MouseWheelStep = 78.0;
	private const double ImageFramePadding = 14.0;
	private const float BackgroundTextureScale = 0.125f; // Cairo pattern matrices use inverse-feeling scale values: lower values make the tiled texture appear larger.
	private const float PanelTextureScale = 0.09375f;
	private const string StackNameColor = "#99c9f9";
	private const string PositiveTraitColor = "#84ff84";
	private const string NegativeTraitColor = "#ff8484";
	private const string ClassNameColor = "#84ff84";
	private const string PlayerModelNameColor = "#ffff84";

	private static readonly Regex StrongOpenTag = new Regex(@"<\s*strong\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex StrongCloseTag = new Regex(@"<\s*/\s*strong\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex ItalicOpenTag = new Regex(@"<\s*i\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex ItalicCloseTag = new Regex(@"<\s*/\s*i\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex BreakTag = new Regex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex FontOpenTag = new Regex(@"<\s*font\s+color\s*=\s*[""']?(#[0-9a-fA-F]{6})[""']?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex FontCloseTag = new Regex(@"<\s*/\s*font\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private RpCharacterCreatorPacket Packet = RpCharacterCreatorPacket.Empty;
	private readonly Dictionary<string, int> PageIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	private int CurrentPageIndex;
	private int CurrentImagePageIndex = -1;
	private LoadedTexture? CurrentImageTexture;
	private Action<string, int, RpCharacterCreatorChoice>? ChoiceSelected;
	private RpCreatorLayout CurrentLayout;
	private float DescScrollValue;
	private float ChoicesScrollValue;
	private float DescContentHeight;
	private float ChoicesContentHeight;
	private bool NeedDescScrollbar;
	private bool NeedChoicesScrollbar;
	private int LastFrameWidth;
	private int LastFrameHeight;

	public override string ToggleKeyCombinationCode => null!;

	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override double DrawOrder => 0.95;
	public override double InputOrder => 0.0;

	public override bool CaptureAllInputs() => true;
	public override bool OnEscapePressed() => false;

	public GuiDialogRpCharacterCreator(ICoreClientAPI capi) : base(capi) { }

	public void SetChoiceSelectedHandler(Action<string, int, RpCharacterCreatorChoice> choiceSelected) { ChoiceSelected = choiceSelected; }

	public void SetPages(RpCharacterCreatorPacket packet)
	{
		Packet = packet ?? RpCharacterCreatorPacket.Empty;
		Packet.Pages ??= Array.Empty<RpCharacterCreatorPage>();

		for (int i = 0; i < Packet.Pages.Length; i++)
		{
			Packet.Pages[i] ??= new RpCharacterCreatorPage();
			Packet.Pages[i].Choices ??= Array.Empty<RpCharacterCreatorChoice>();
			Packet.Pages[i].ImageBytes ??= Array.Empty<byte>();

			for (int j = 0; j < Packet.Pages[i].Choices.Length; j++)
			{
				Packet.Pages[i].Choices[j] ??= new RpCharacterCreatorChoice();
				Packet.Pages[i].Choices[j].StackReward ??= Array.Empty<RpStackReward>();
				Packet.Pages[i].Choices[j].TraitReward ??= Array.Empty<string>();
				Packet.Pages[i].Choices[j].CustomRepercussion ??= Array.Empty<string>();
			}
		}

		BuildPageIndex();
		DisposeCurrentImageTexture();
		ResetScrollState();

		if (!string.IsNullOrWhiteSpace(Packet.StartPageId) && PageIndexById.TryGetValue(Packet.StartPageId, out int startIndex)) { CurrentPageIndex = startIndex; }
		else { CurrentPageIndex = 0; }

		if (IsOpened()) { Compose(); }
	}

	public override void OnGuiOpened()
	{
		Compose();
		base.OnGuiOpened();
	}

	public override void OnRenderGUI(float deltaTime)
	{
		if (LastFrameWidth != capi.Render.FrameWidth || LastFrameHeight != capi.Render.FrameHeight) { Compose(); }

		Composers["background"]?.Render(deltaTime);
		RenderCurrentPageImage();
		Composers["content"]?.Render(deltaTime);

		MouseOverCursor = Composers["content"]?.MouseOverCursor;
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		if (!IsOpened() || args.IsHandled) return;

		int mouseX = capi.Input.MouseX;
		int mouseY = capi.Input.MouseY;

		if (NeedDescScrollbar && CurrentLayout.DescPanel != null && CurrentLayout.DescScrollbar != null && CurrentLayout.DescClip != null
			&& (CurrentLayout.DescPanel.PointInside(mouseX, mouseY) || CurrentLayout.DescScrollbar.PointInside(mouseX, mouseY)))
		{
			SetDescScroll(DescScrollValue - (float)(args.deltaPrecise * MouseWheelStep));
			args.SetHandled();
			return;
		}

		if (NeedChoicesScrollbar && CurrentLayout.ChoicesPanel != null && CurrentLayout.ChoicesScrollbar != null && CurrentLayout.ChoicesClip != null
			&& (CurrentLayout.ChoicesPanel.PointInside(mouseX, mouseY) || CurrentLayout.ChoicesScrollbar.PointInside(mouseX, mouseY)))
		{
			SetChoicesScroll(ChoicesScrollValue - (float)(args.deltaPrecise * MouseWheelStep));
			args.SetHandled();
		}
	}

	private void BuildPageIndex()
	{
		PageIndexById.Clear();

		for (int i = 0; i < Packet.Pages.Length; i++)
		{
			string id = Packet.Pages[i].Id;
			if (!string.IsNullOrWhiteSpace(id)) { PageIndexById[id] = i; }
		}
	}

	private void Compose()
	{
		ClearComposers();

		LastFrameWidth = capi.Render.FrameWidth;
		LastFrameHeight = capi.Render.FrameHeight;

		RpCharacterCreatorPage page = GetCurrentPage();
		bool hasImage = GetCurrentPageImageTexture() is LoadedTexture texture && texture.TextureId != 0;
		string descVtml = SanitizeLimitedVtml(page.Desc);
		CairoFont descFont = CreateDescFont();

		CurrentLayout = CalculateLayout(hasImage, reserveDescScrollbar: false, reserveChoicesScrollbar: false);
		DescContentHeight = MeasureRichtextHeight(descVtml, descFont, CurrentLayout.DescText.fixedWidth);
		ChoicesContentHeight = MeasureChoicesHeight(page, CurrentLayout.ChoicesClip.fixedWidth);

		NeedDescScrollbar = DescContentHeight > CurrentLayout.DescClip.fixedHeight + 1.0f;
		NeedChoicesScrollbar = ChoicesContentHeight > CurrentLayout.ChoicesClip.fixedHeight + 1.0f;

		CurrentLayout = CalculateLayout(hasImage, NeedDescScrollbar, NeedChoicesScrollbar);
		DescContentHeight = MeasureRichtextHeight(descVtml, descFont, CurrentLayout.DescText.fixedWidth);
		ChoicesContentHeight = MeasureChoicesHeight(page, CurrentLayout.ChoicesClip.fixedWidth);

		NeedDescScrollbar = DescContentHeight > CurrentLayout.DescClip.fixedHeight + 1.0f;
		NeedChoicesScrollbar = ChoicesContentHeight > CurrentLayout.ChoicesClip.fixedHeight + 1.0f;

		DescScrollValue = Math.Clamp(DescScrollValue, 0.0f, Math.Max(0.0f, DescContentHeight - (float)CurrentLayout.DescClip.fixedHeight));
		ChoicesScrollValue = Math.Clamp(ChoicesScrollValue, 0.0f, Math.Max(0.0f, ChoicesContentHeight - (float)CurrentLayout.ChoicesClip.fixedHeight));

		ComposeBackground();

		GuiComposer composer = capi.Gui.CreateCompo("yangrpcharcreator-content", ElementBounds.Fill)
			.BeginClip(CurrentLayout.DescClip)
				.AddRichtext(descVtml, descFont, CurrentLayout.DescText, "desc")
			.EndClip();

		if (NeedDescScrollbar) { composer.AddVerticalScrollbar(OnDescScrollbar, CurrentLayout.DescScrollbar, "descScrollbar"); }
		composer.BeginClip(CurrentLayout.ChoicesClip).BeginChildElements(CurrentLayout.ChoicesList);
		ComposeChoiceButtons(composer, page);

		composer.EndChildElements().EndClip();

		if (NeedChoicesScrollbar) { composer.AddVerticalScrollbar(OnChoicesScrollbar, CurrentLayout.ChoicesScrollbar, "choicesScrollbar"); }
		Composers["content"] = composer.Compose();

		UpdateScrollbarHeights();
	}

	private void ComposeBackground()
	{
		GuiComposer background = capi.Gui.CreateCompo("yangrpcharcreator-background", ElementBounds.Fill)
			.AddStaticCustomDraw(ElementBounds.Fill, DrawParchmentBackground)
			.AddStaticCustomDraw(CurrentLayout.ContentPanel, DrawParchmentPanel)
			.AddStaticCustomDraw(CurrentLayout.DescPanel, DrawParchmentInset)
			.AddStaticCustomDraw(CurrentLayout.ChoicesPanel, DrawParchmentInset);

		if (CurrentLayout.HasImage)
		{
			background
				.AddStaticCustomDraw(CurrentLayout.ImagePanel, DrawImagePanel)
				.AddStaticCustomDraw(CurrentLayout.ImageContent, DrawImageContentMatte);
		}

		Composers["background"] = background.Compose();
	}

	private RpCreatorLayout CalculateLayout(bool hasImage, bool reserveDescScrollbar, bool reserveChoicesScrollbar)
	{
		double guiScale = Math.Max(0.1, RuntimeEnv.GUIScale);
		double screenWidth = Math.Max(MinScreenWidth, capi.Render.FrameWidth / guiScale);
		double screenHeight = Math.Max(MinScreenHeight, capi.Render.FrameHeight / guiScale);
		double outerMargin = Clamp(screenWidth * 0.04, 24.0, 70.0);
		double verticalMargin = Clamp(screenHeight * 0.05, 24.0, 60.0);
		double columnGap = Clamp(screenWidth * 0.025, 20.0, 32.0);

		double contentWidth = 0;
		double contentHeight = Math.Min(820.0, Math.Max(320.0, screenHeight - verticalMargin * 2.0));
		double contentX = 0;
		double contentY = (screenHeight - contentHeight) / 2.0;
		double imageWidth = 0.0;
		double imageX = 0.0;

		if (hasImage)
		{
			imageWidth = Clamp(screenWidth * 0.32, MinImageWidth, MaxImageWidth);
			double available = screenWidth - outerMargin * 2.0;
			contentWidth = Math.Min(760.0, available - imageWidth - columnGap);

			if (contentWidth < MinContentWidth || screenHeight < 500.0) { hasImage = false; }
			else
			{
				double totalWidth = contentWidth + columnGap + imageWidth;
				contentX = (screenWidth - totalWidth) / 2.0;
				imageX = contentX + contentWidth + columnGap;
			}
		}

		if (!hasImage)
		{
			contentWidth = Clamp(screenWidth * 0.72, Math.Min(480.0, screenWidth - outerMargin * 2.0), MaxContentWidth);
			contentWidth = Math.Min(contentWidth, screenWidth - outerMargin * 2.0);
			contentX = (screenWidth - contentWidth) / 2.0;
		}

		double innerX = contentX + ContentPaddingX;
		double innerY = contentY + ContentPaddingY;
		double innerWidth = Math.Max(260.0, contentWidth - ContentPaddingX * 2.0);
		double innerHeight = Math.Max(240.0, contentHeight - ContentPaddingY * 2.0);

		double descHeight = Math.Floor(innerHeight * 0.60);
		double choicesHeight = innerHeight - descHeight - SectionGap;
		if (choicesHeight < 120.0)
		{
			choicesHeight = Math.Min(120.0, innerHeight * 0.42);
			descHeight = Math.Max(110.0, innerHeight - choicesHeight - SectionGap);
		}

		double descAreaWidth = Math.Max(180.0, innerWidth - (reserveDescScrollbar ? ScrollbarWidth + ScrollbarGap : 0.0));
		double choicesAreaWidth = Math.Max(180.0, innerWidth - (reserveChoicesScrollbar ? ScrollbarWidth + ScrollbarGap : 0.0));
		double choicesY = innerY + descHeight + SectionGap;

		ElementBounds contentPanel = ElementBounds.Fixed(contentX, contentY, contentWidth, contentHeight);
		ElementBounds descPanel = ElementBounds.Fixed(innerX - 10.0, innerY - 8.0, innerWidth + 20.0, descHeight + 16.0);
		ElementBounds descClip = ElementBounds.Fixed(innerX, innerY, descAreaWidth, descHeight);
		ElementBounds descText = ElementBounds.Fixed(0.0, -DescScrollValue, descAreaWidth, 1.0);
		ElementBounds descScrollbar = reserveDescScrollbar
			? ElementBounds.Fixed(innerX + descAreaWidth + ScrollbarGap, innerY, ScrollbarWidth, descHeight).WithFixedPadding(2.0)
			: ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0);

		ElementBounds choicesPanel = ElementBounds.Fixed(innerX - 10.0, choicesY - 8.0, innerWidth + 20.0, choicesHeight + 16.0);
		ElementBounds choicesClip = ElementBounds.Fixed(innerX, choicesY, choicesAreaWidth, choicesHeight);
		ElementBounds choicesList = ElementBounds.Fixed(0.0, -ChoicesScrollValue, choicesAreaWidth, choicesHeight);
		ElementBounds choicesScrollbar = reserveChoicesScrollbar
			? ElementBounds.Fixed(innerX + choicesAreaWidth + ScrollbarGap, choicesY, ScrollbarWidth, choicesHeight).WithFixedPadding(2.0)
			: ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0);

		ElementBounds imagePanel = hasImage
			? ElementBounds.Fixed(imageX, contentY, imageWidth, contentHeight)
			: ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0);
		ElementBounds imageContent = hasImage
			? ElementBounds.Fixed
			(
				imageX + ImageFramePadding, contentY + ImageFramePadding,
				Math.Max(1.0, imageWidth - ImageFramePadding * 2.0),
				Math.Max(1.0, contentHeight - ImageFramePadding * 2.0)
			)
			: ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0);

		return new RpCreatorLayout
		{
			HasImage = hasImage,
			ContentPanel = contentPanel,
			DescPanel = descPanel,
			DescClip = descClip,
			DescText = descText,
			DescScrollbar = descScrollbar,
			ChoicesPanel = choicesPanel,
			ChoicesClip = choicesClip,
			ChoicesList = choicesList,
			ChoicesScrollbar = choicesScrollbar,
			ImagePanel = imagePanel,
			ImageContent = imageContent
		};
	}

	private void ComposeChoiceButtons(GuiComposer composer, RpCharacterCreatorPage page)
	{
		double y = 0.0;

		if (page.Choices.Length == 0)
		{
			double innerHeight = GetChoiceButtonInnerHeight("Continue", CurrentLayout.ChoicesClip.fixedWidth);
			ElementBounds bounds = CreateChoiceButtonBounds(y, innerHeight);
			AddChoiceButton(composer, "Continue", OnNoChoiceContinueClicked, bounds, "choice-continue");
			y += bounds.fixedHeight + ChoiceButtonPadY * 2.0;
		}
		else
		{
			for (int i = 0; i < page.Choices.Length; i++)
			{
				int choiceIndex = i;
				string title = page.Choices[i].Title;
				string tooltip = BuildRepercussionTooltip(page.Choices[i]);
				double innerHeight = GetChoiceButtonInnerHeight(title, CurrentLayout.ChoicesClip.fixedWidth);
				ElementBounds bounds = CreateChoiceButtonBounds(y, innerHeight);

				AddChoiceButton(composer, title, () => OnChoiceClicked(choiceIndex), bounds, "choice-" + i, tooltip);
				y += bounds.fixedHeight + ChoiceButtonPadY * 2.0 + ChoiceGap;
			}

			if (y > 0.0) { y -= ChoiceGap; }
		}

		ChoicesContentHeight = (float)Math.Max(CurrentLayout.ChoicesClip.fixedHeight, y);
		CurrentLayout.ChoicesList.fixedHeight = ChoicesContentHeight;
	}

	private void AddChoiceButton(GuiComposer composer, string title, ActionConsumable onClick, ElementBounds bounds, string key, string tooltip = "")
	{
		composer.AddInteractiveElement(new RpChoiceButton(capi, SanitizeChoiceButtonVtml(title), CreateChoiceFont(), onClick, bounds), key);

		if (!string.IsNullOrWhiteSpace(tooltip)) { composer.AddHoverText(tooltip, CreateTooltipFont(), 420, bounds.FlatCopy(), "tooltip-" + key); }
	}

	private ElementBounds CreateChoiceButtonBounds(double y, double innerHeight)
	{
		double innerButtonWidth = Math.Max(120.0, CurrentLayout.ChoicesClip.fixedWidth - ChoiceButtonPadX * 2.0);
		return ElementBounds.Fixed(0.0, y, innerButtonWidth, innerHeight).WithFixedPadding(ChoiceButtonPadX, ChoiceButtonPadY);
	}

	private float MeasureChoicesHeight(RpCharacterCreatorPage page, double clipWidth)
	{
		double y = 0.0;

		if (page.Choices.Length == 0)
		{
			double innerHeight = GetChoiceButtonInnerHeight("Continue", clipWidth);
			return (float)(innerHeight + ChoiceButtonPadY * 2.0);
		}

		for (int i = 0; i < page.Choices.Length; i++)
		{
			double innerHeight = GetChoiceButtonInnerHeight(page.Choices[i].Title, clipWidth);
			y += innerHeight + ChoiceButtonPadY * 2.0 + ChoiceGap;
		}

		if (y > 0.0) { y -= ChoiceGap; }
		return (float)y;
	}

	private double GetChoiceButtonInnerHeight(string title, double clipWidth)
	{
		double innerButtonWidth = Math.Max(120.0, clipWidth - ChoiceButtonPadX * 2.0);
		double textWidth = Math.Max(80.0, innerButtonWidth - 12.0);
		int lineCount = EstimateChoiceLineCount(title, CreateChoiceFont(), textWidth);
		return lineCount > 1 ? ChoiceButtonDoubleInnerHeight : ChoiceButtonInnerHeight;
	}

	private int EstimateChoiceLineCount(string title, CairoFont font, double maxWidth)
	{
		string normalized = StripLimitedVtmlTags(title).Replace("\r\n", "\n").Replace("\r", "\n").Trim();
		if (normalized.Length == 0) return 1;

		TextDrawUtil textDrawUtil = new TextDrawUtil();
		TextLine[] lines = textDrawUtil.Lineize(font, normalized, maxWidth, EnumLinebreakBehavior.Default);
		return Math.Clamp(lines.Length, 1, 2);
	}

	private static string StripLimitedVtmlTags(string text)
	{
		text ??= "";
		text = BreakTag.Replace(text, "\n");
		text = StrongOpenTag.Replace(text, "");
		text = StrongCloseTag.Replace(text, "");
		text = ItalicOpenTag.Replace(text, "");
		text = ItalicCloseTag.Replace(text, "");
		text = FontOpenTag.Replace(text, "");
		text = FontCloseTag.Replace(text, "");
		return text;
	}

	private float MeasureRichtextHeight(string vtmlCode, CairoFont font, double width)
	{
		ElementBounds measureBounds = ElementBounds.Fixed(0.0, 0.0, width, 1.0);
		GuiElementRichtext richtext = new GuiElementRichtext(capi, VtmlUtil.Richtextify(capi, vtmlCode, font), measureBounds);
		richtext.CalcHeightAndPositions();
		float height = (float)Math.Max(1.0, richtext.Bounds.fixedHeight);
		richtext.Dispose();
		return height;
	}

	private void UpdateScrollbarHeights()
	{
		GuiComposer composer = Composers["content"]; if (composer == null) return;
		GuiElementRichtext desc = composer.GetRichtext("desc"); if (desc != null) { DescContentHeight = (float)Math.Max(DescContentHeight, desc.Bounds.fixedHeight); }

		if (NeedDescScrollbar) { SetScrollbarHeight(composer.GetScrollbar("descScrollbar"), CurrentLayout.DescClip.fixedHeight, DescContentHeight); }
		if (NeedChoicesScrollbar) { SetScrollbarHeight(composer.GetScrollbar("choicesScrollbar"), CurrentLayout.ChoicesClip.fixedHeight, ChoicesContentHeight); }

		SetDescScroll(DescScrollValue);
		SetChoicesScroll(ChoicesScrollValue);
	}

	private void SetScrollbarHeight(GuiElementScrollbar? scrollbar, double visibleHeight, float totalHeight)
	{
		if (scrollbar == null) return;

		scrollbar.SetHeights((float)visibleHeight, Math.Max((float)visibleHeight, totalHeight));
	}

	private void SetDescScroll(float value)
	{
		if (!NeedDescScrollbar)
		{
			DescScrollValue = 0.0f;
			OnDescScrollbar(0.0f);
			return;
		}

		float maxScroll = Math.Max(0.0f, DescContentHeight - (float)CurrentLayout.DescClip.fixedHeight);
		DescScrollValue = Math.Clamp(value, 0.0f, maxScroll);
		OnDescScrollbar(DescScrollValue);
		SetScrollbarPosition("descScrollbar", DescScrollValue);
	}

	private void SetChoicesScroll(float value)
	{
		if (!NeedChoicesScrollbar)
		{
			ChoicesScrollValue = 0.0f;
			OnChoicesScrollbar(0.0f);
			return;
		}

		float maxScroll = Math.Max(0.0f, ChoicesContentHeight - (float)CurrentLayout.ChoicesClip.fixedHeight);
		ChoicesScrollValue = Math.Clamp(value, 0.0f, maxScroll);
		OnChoicesScrollbar(ChoicesScrollValue);
		SetScrollbarPosition("choicesScrollbar", ChoicesScrollValue);
	}

	private void SetScrollbarPosition(string key, float value)
	{
		GuiComposer composer = Composers["content"];
		GuiElementScrollbar? scrollbar = composer?.GetScrollbar(key);
		if (scrollbar == null) return;

		scrollbar.CurrentYPosition = value;
		scrollbar.RecomposeHandle();
	}

	private void OnDescScrollbar(float value)
	{
		DescScrollValue = Math.Clamp(value, 0.0f, Math.Max(0.0f, DescContentHeight - (float)CurrentLayout.DescClip.fixedHeight));
		CurrentLayout.DescText.fixedY = -DescScrollValue;
		CurrentLayout.DescText.MarkDirtyRecursive();
		CurrentLayout.DescText.CalcWorldBounds();
	}

	private void OnChoicesScrollbar(float value)
	{
		ChoicesScrollValue = Math.Clamp(value, 0.0f, Math.Max(0.0f, ChoicesContentHeight - (float)CurrentLayout.ChoicesClip.fixedHeight));
		CurrentLayout.ChoicesList.fixedY = -ChoicesScrollValue;
		CurrentLayout.ChoicesList.MarkDirtyRecursive();
		CurrentLayout.ChoicesList.CalcWorldBounds();
	}

	private RpCharacterCreatorPage GetCurrentPage()
	{
		if (Packet.Pages.Length == 0)
		{
			return new RpCharacterCreatorPage
			{
				Id = "empty",
				Desc = "No character creator pages were received from the server.",
				Choices = new[] { new RpCharacterCreatorChoice { Title = "Finish", Completed = true } }
			};
		}

		CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Packet.Pages.Length - 1);
		return Packet.Pages[CurrentPageIndex];
	}

	private void RenderCurrentPageImage()
	{
		if (!CurrentLayout.HasImage) return;

		LoadedTexture? texture = GetCurrentPageImageTexture();
		if (texture == null || texture.TextureId == 0 || texture.Width <= 0 || texture.Height <= 0) return;
		if (!TryGetImageDrawRect(texture, out double drawX, out double drawY, out double drawWidth, out double drawHeight)) return;

		CurrentLayout.ImageContent.CalcWorldBounds();
		capi.Render.PushScissor(CurrentLayout.ImageContent);
		try { capi.Render.Render2DTexture(texture.TextureId, (float)drawX, (float)drawY, (float)drawWidth, (float)drawHeight, 100.0f); }
		finally { capi.Render.PopScissor(); }
	}

	private bool TryGetImageDrawRect(LoadedTexture texture, out double drawX, out double drawY, out double drawWidth, out double drawHeight)
	{
		drawX = drawY = drawWidth = drawHeight = 0.0;

		if (texture.Width <= 0 || texture.Height <= 0 || CurrentLayout.ImageContent == null) return false;

		ElementBounds content = CurrentLayout.ImageContent;
		content.CalcWorldBounds();

		double areaX = content.renderX;
		double areaY = content.renderY;
		double areaWidth = Math.Max(1.0, content.InnerWidth);
		double areaHeight = Math.Max(1.0, content.InnerHeight);

		double scale = Math.Max(areaWidth / texture.Width, areaHeight / texture.Height);
		drawWidth = texture.Width * scale;
		drawHeight = texture.Height * scale;
		drawX = areaX + (areaWidth - drawWidth) / 2.0;
		drawY = areaY + (areaHeight - drawHeight) / 2.0;

		return drawWidth > 0.0 && drawHeight > 0.0;
	}

	private LoadedTexture? GetCurrentPageImageTexture()
	{
		if (CurrentImagePageIndex == CurrentPageIndex) return CurrentImageTexture;

		DisposeCurrentImageTexture();
		CurrentImagePageIndex = CurrentPageIndex;

		RpCharacterCreatorPage page = GetCurrentPage();
		if (page.ImageBytes.Length == 0) return null;

		try
		{
			using BitmapExternal bitmap = new BitmapExternal(page.ImageBytes, page.ImageBytes.Length, capi.Logger);
			LoadedTexture texture = new LoadedTexture(capi);
			capi.Render.LoadTexture(bitmap, ref texture, linearMag: true);

			CurrentImageTexture = texture;
			return CurrentImageTexture;
		}
		catch (Exception ex)
		{
			capi.Logger.Warning("[yangrpcharcreator] Could not decode/upload image '{0}' for page '{1}': {2}", page.Image, page.Id, ex.Message);
			return null;
		}
	}

	private bool OnChoiceClicked(int choiceIndex)
	{
		RpCharacterCreatorPage page = GetCurrentPage();
		if (choiceIndex < 0 || choiceIndex >= page.Choices.Length) return true;

		RpCharacterCreatorChoice choice = page.Choices[choiceIndex];
		ChoiceSelected?.Invoke(page.Id, choiceIndex, choice);

		if (choice.CharacterCreate)
		{
			TryClose();
			return true;
		}

		if (choice.Completed)
		{
			TryClose();
			return true;
		}

		if (!string.IsNullOrWhiteSpace(choice.Goto) && PageIndexById.TryGetValue(choice.Goto, out int gotoIndex)) { CurrentPageIndex = gotoIndex; }
		else { CurrentPageIndex++; }

		if (CurrentPageIndex >= Packet.Pages.Length)
		{
			TryClose();
			return true;
		}

		ResetScrollState();
		Compose();
		return true;
	}

	private bool OnNoChoiceContinueClicked()
	{
		RpCharacterCreatorPage page = GetCurrentPage();
		ChoiceSelected?.Invoke(page.Id, -1, new RpCharacterCreatorChoice());

		CurrentPageIndex++;

		if (CurrentPageIndex >= Packet.Pages.Length)
		{
			TryClose();
			return true;
		}

		ResetScrollState();
		Compose();
		return true;
	}

	private CairoFont CreateDescFont()
	{
		return CairoFont.WhiteSmallishText().WithColor(new double[] { 0.19, 0.13, 0.075, 1.0 }).WithLineHeightMultiplier(1.18);
	}

	private CairoFont CreateChoiceFont()
	{
		return CairoFont.ButtonText().WithColor(new double[] { 0.19, 0.13, 0.075, 1.0 }).WithLineHeightMultiplier(1.05);
	}

	private CairoFont CreateTooltipFont()
	{
		return CairoFont.WhiteSmallText().WithLineHeightMultiplier(1.08);
	}

	private string BuildRepercussionTooltip(RpCharacterCreatorChoice choice)
	{
		List<string> lines = new List<string>();

		string[] customLines = choice.CustomRepercussion ?? Array.Empty<string>();
		bool hasCustomRepercussions = HasCustomRepercussions(customLines);

		if (choice.ShowRepercussions)
		{
			int lineCountBeforeAutoRepercussions = lines.Count;
			AddAutoRepercussionLines(lines, choice);

			if (lines.Count == lineCountBeforeAutoRepercussions && !hasCustomRepercussions)
			{
				lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-continue")));
			}
		}
		else { lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-unknown"))); }

		for (int i = 0; i < customLines.Length; i++)
		{
			string line = (customLines[i] ?? "").Trim();
			if (line.Length > 0) { lines.Add(Bulletize(line)); }
		}

		return lines.Count == 0 ? "" : string.Join("<br>", lines);
	}

	private static bool HasCustomRepercussions(string[] customLines)
	{
		for (int i = 0; i < customLines.Length; i++) { if (!string.IsNullOrWhiteSpace(customLines[i])) return true; }

		return false;
	}

	private void AddAutoRepercussionLines(List<string> lines, RpCharacterCreatorChoice choice)
	{
		RpStackReward[] stackRewards = choice.StackReward ?? Array.Empty<RpStackReward>();
		for (int i = 0; i < stackRewards.Length; i++)
		{
			RpStackReward reward = stackRewards[i];
			string stackName = ResolveStackRewardName(reward);
			int stackSize = Math.Max(1, reward.StackSize);

			lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-stack", stackSize, ColorizeStackName(stackName))));
		}

		string[] traitRewards = choice.TraitReward ?? Array.Empty<string>();
		if (traitRewards.Length == 1)
		{
			lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-trait-one", ResolveTraitName(traitRewards[0]))));
		}
		else if (traitRewards.Length > 1)
		{
			string[] traitNames = new string[traitRewards.Length];
			for (int i = 0; i < traitRewards.Length; i++) { traitNames[i] = ResolveTraitName(traitRewards[i]); }

			lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-trait-many", string.Join(", ", traitNames))));
		}

		if (!string.IsNullOrWhiteSpace(choice.ClassReward))
		{
			string className = ColorizeText(ResolveClassName(choice.ClassReward), ClassNameColor);
			lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-class", className)));
		}

		if (!string.IsNullOrWhiteSpace(choice.SetPlayerLib))
		{
			string modelName = ColorizeText(PlayerModelLibVisualInterop.ResolveModelDisplayName(capi, choice.SetPlayerLib), PlayerModelNameColor);
			lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-player-model", modelName)));
		}

		if (choice.CharacterCreate) { lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-character-create"))); }
		if (choice.Completed) { lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-completed"))); }
		if (!string.IsNullOrWhiteSpace(choice.SetLocation)) { lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-setlocation"))); }
		if (!string.IsNullOrWhiteSpace(choice.SetSpawn)) { lines.Add(Bulletize(Lang.Get("yangrpcharactercreator:repercussion-setspawn"))); }
	}

	private string ResolveStackRewardName(RpStackReward reward)
	{
		string rawCode = (reward.Code ?? "").Trim();
		string type = (reward.Type ?? "").Trim().ToLowerInvariant();
		if (rawCode.Length == 0) return Lang.Get("yangrpcharactercreator:repercussion-unknown-stack");

		try
		{
			AssetLocation code = new AssetLocation(rawCode);

			if (type == "item")
			{
				Item item = capi.World.GetItem(code);
				if (item != null && !item.IsMissing) { return StripLimitedVtmlTags(new ItemStack(item, 1).GetName()).Trim(); }
			}

			if (type == "block")
			{
				Block block = capi.World.GetBlock(code);
				if (block != null && !block.IsMissing) { return StripLimitedVtmlTags(new ItemStack(block, 1).GetName()).Trim(); }
			}
		}
		catch { }

		return rawCode;
	}

	private string ResolveTraitName(string traitCode)
	{
		traitCode = (traitCode ?? "").Trim();
		if (traitCode.Length == 0) return traitCode;

		string processedCode = ProcessDomainForLang(traitCode);
		string? name =
			GetDomainLangIfExists("traitname-", traitCode)
			?? Lang.GetIfExists("traitname-" + processedCode)
			?? Lang.GetIfExists("traitname-" + traitCode);

		// Fallback for mods that only define the older/full trait localization key.
		// Strip it because vanilla's trait-* values include their own bullet and padding.
		if (string.IsNullOrWhiteSpace(name))
		{
			name =
				GetDomainLangIfExists("trait-", traitCode)
				?? Lang.GetIfExists("trait-" + processedCode)
				?? Lang.GetIfExists("trait-" + traitCode);
		}

		string cleanName = StripLimitedVtmlTags(name ?? traitCode).Trim().TrimStart('•').Trim();
		return ColorizeText(cleanName.Length > 0 ? cleanName : traitCode, ResolveTraitColor(traitCode));
	}

	private string ResolveTraitColor(string traitCode)
	{
		if (!TryGetTrait(traitCode, out Trait? trait)) return "";

		return trait.Type == EnumTraitType.Negative ? NegativeTraitColor : PositiveTraitColor;
	}

	private bool TryGetTrait(string traitCode, out Trait? trait)
	{
		trait = null;

		CharacterSystem? characterSystem = capi.ModLoader.GetModSystem<CharacterSystem>();
		if (characterSystem == null) return false;

		List<string> candidateCodes = GetCodeLookupCandidates(traitCode);
		for (int i = 0; i < candidateCodes.Count; i++) { if (characterSystem.TraitsByCode.TryGetValue(candidateCodes[i], out trait)) return true; }

		return false;
	}

	private string ResolveClassName(string classCode)
	{
		classCode = (classCode ?? "").Trim();
		if (classCode.Length == 0) return classCode;

		string processedCode = ProcessDomainForLang(classCode);
		string? name =
			GetDomainLangIfExists("characterclass-", classCode)
			?? Lang.GetIfExists("characterclass-" + processedCode)
			?? Lang.GetIfExists("characterclass-" + classCode);

		return (name ?? classCode).Trim();
	}

	private static string? GetDomainLangIfExists(string prefix, string code)
	{
		if (string.IsNullOrWhiteSpace(code) || !code.Contains(":")) return null;

		try
		{
			AssetLocation location = new AssetLocation(code);
			return Lang.GetIfExists(location.Domain + ":" + prefix + location.Path);
		}
		catch { return null; }
	}

	private static string ProcessDomainForLang(string code) { return (code ?? "").Replace("game:", "").Replace(':', '-'); }

	private static List<string> GetCodeLookupCandidates(string code)
	{
		List<string> candidates = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Add(string candidate)
		{
			candidate = (candidate ?? "").Trim();
			if (candidate.Length > 0 && seen.Add(candidate)) { candidates.Add(candidate); }
		}

		Add(code);

		try
		{
			AssetLocation location = new AssetLocation(code);
			Add(location.Path);
			Add(location.Domain + ":" + location.Path);
		}
		catch { } // Ignore malformed asset locations and fall back to the raw code.

		Add(ProcessDomainForLang(code));

		return candidates;
	}

	private static string ColorizeStackName(string text) { return ColorizeText(text, StackNameColor); }

	private static string ColorizeText(string text, string color)
	{
		text = (text ?? "").Trim();
		color = (color ?? "").Trim();

		return color.Length == 0 ? text : "<font color=\"" + color + "\">" + text + "</font>";
	}

	private static string Bulletize(string text) { return Lang.Get("yangrpcharactercreator:repercussion-bullet", SanitizeLimitedVtml(text)); }

	private static string SanitizeChoiceButtonVtml(string text)
	{
		text ??= "";

		// Choice buttons already use CairoFont.ButtonText(), which is bold by default.
		// Keeping <strong> inside button labels causes richtext fragments to overlap, so it is intentionally ignored only for choice button text.
		text = StrongOpenTag.Replace(text, "");
		text = StrongCloseTag.Replace(text, "");

		return SanitizeLimitedVtml(text);
	}

	private static string SanitizeLimitedVtml(string text)
	{
		text ??= "";

		const string strongOpen = "\uE000";
		const string strongClose = "\uE001";
		const string italicOpen = "\uE002";
		const string italicClose = "\uE003";
		const string br = "\uE004";
		const string fontOpenPrefix = "\uE005";
		const string fontOpenSuffix = "\uE006";
		const string fontClose = "\uE007";

		text = StrongOpenTag.Replace(text, strongOpen);
		text = StrongCloseTag.Replace(text, strongClose);
		text = ItalicOpenTag.Replace(text, italicOpen);
		text = ItalicCloseTag.Replace(text, italicClose);
		text = BreakTag.Replace(text, br);
		text = FontOpenTag.Replace(text, match => fontOpenPrefix + match.Groups[1].Value + fontOpenSuffix);
		text = FontCloseTag.Replace(text, fontClose);

		text = text
			.Replace("&", "&amp;")
			.Replace("<", "&lt;")
			.Replace(">", "&gt;")
			.Replace("\r\n", "\n")
			.Replace("\r", "\n")
			.Replace("\n", "<br>");

		text = text
			.Replace(strongOpen, "<strong>")
			.Replace(strongClose, "</strong>")
			.Replace(italicOpen, "<i>")
			.Replace(italicClose, "</i>")
			.Replace(br, "<br>")
			.Replace(fontClose, "</font>");

		return Regex.Replace(text, Regex.Escape(fontOpenPrefix) + "(#[0-9a-fA-F]{6})" + Regex.Escape(fontOpenSuffix), "<font color=\"$1\">");
	}

	private void DrawParchmentBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
	{
		ctx.SetSourceRGBA(0.74, 0.58, 0.36, 1.0);
		ctx.Paint();

		SurfacePattern pattern = GuiElement.getPattern(capi, GuiElement.dirtTextureName, doCache: true, 95, BackgroundTextureScale);
		ctx.SetSource((Pattern)pattern);
		ctx.Rectangle(bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight);
		ctx.Fill();

		double edge = GuiElement.scaled(26.0);
		ctx.SetSourceRGBA(0.12, 0.07, 0.025, 0.20);
		ctx.Rectangle(bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, edge);
		ctx.Rectangle(bounds.bgDrawX, bounds.bgDrawY + bounds.OuterHeight - edge, bounds.OuterWidth, edge);
		ctx.Rectangle(bounds.bgDrawX, bounds.bgDrawY, edge, bounds.OuterHeight);
		ctx.Rectangle(bounds.bgDrawX + bounds.OuterWidth - edge, bounds.bgDrawY, edge, bounds.OuterHeight);
		ctx.Fill();
	}

	private void DrawParchmentPanel(Context ctx, ImageSurface surface, ElementBounds bounds) { DrawPanelBase(ctx, bounds, 0.86, 0.70, 0.46, 0.88, 8.0); }

	private void DrawParchmentInset(Context ctx, ImageSurface surface, ElementBounds bounds) { DrawPanelBase(ctx, bounds, 0.93, 0.78, 0.52, 0.48, 4.0); }

	private void DrawImagePanel(Context ctx, ImageSurface surface, ElementBounds bounds)
	{
		DrawPanelBase(ctx, bounds, 0.86, 0.70, 0.46, 0.34, 8.0);

		double pad = GuiElement.scaled(ImageFramePadding);
		double frameOutset = GuiElement.scaled(4.0);
		GuiElement.RoundRectangle(
			ctx,
			bounds.bgDrawX + pad - frameOutset,
			bounds.bgDrawY + pad - frameOutset,
			bounds.OuterWidth - pad * 2.0 + frameOutset * 2.0,
			bounds.OuterHeight - pad * 2.0 + frameOutset * 2.0,
			GuiElement.scaled(4.0)
		);
		ctx.SetSourceRGBA(0.20, 0.12, 0.055, 0.72);
		ctx.FillPreserve();
		ctx.SetSourceRGBA(0.56, 0.38, 0.18, 0.88);
		ctx.LineWidth = GuiElement.scaled(2.0);
		ctx.Stroke();
	}

	private void DrawImageContentMatte(Context ctx, ImageSurface surface, ElementBounds bounds)
	{
		GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(2.0));
		ctx.SetSourceRGBA(0.06, 0.04, 0.025, 0.78);
		ctx.Fill();
	}

	private void DrawPanelBase(Context ctx, ElementBounds bounds, double r, double g, double b, double a, double radius)
	{
		GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(radius));
		ctx.SetSourceRGBA(r, g, b, a);
		ctx.FillPreserve();

		SurfacePattern pattern = GuiElement.getPattern(capi, GuiElement.dirtTextureName, doCache: true, 64, PanelTextureScale);
		ctx.SetSource((Pattern)pattern);
		ctx.FillPreserve();

		ctx.SetSourceRGBA(0.23, 0.14, 0.06, 0.45);
		ctx.LineWidth = GuiElement.scaled(2.0);
		ctx.Stroke();
	}

	private void ResetScrollState()
	{
		DescScrollValue = 0.0f;
		ChoicesScrollValue = 0.0f;
		DescContentHeight = 0.0f;
		ChoicesContentHeight = 0.0f;
		NeedDescScrollbar = false;
		NeedChoicesScrollbar = false;
	}

	private void DisposeCurrentImageTexture()
	{
		CurrentImageTexture?.Dispose();
		CurrentImageTexture = null;
		CurrentImagePageIndex = -1;
	}

	private static double Clamp(double value, double min, double max)
	{
		if (max < min) return min;
		return Math.Clamp(value, min, max);
	}

	public override void Dispose()
	{
		DisposeCurrentImageTexture();
		base.Dispose();
	}


	private sealed class RpChoiceButton : GuiElementControl
	{
		private readonly ActionConsumable onClick;
		private readonly GuiElementRichtext textElement;
		private int normalTexture;
		private int hoverTexture;
		private int activeTexture;
		private bool isOver;
		private bool mouseDownOnElement;

		public override bool Focusable => enabled;

		public RpChoiceButton(ICoreClientAPI capi, string vtmlText, CairoFont font, ActionConsumable onClick, ElementBounds bounds) : base(capi, bounds)
		{
			this.onClick = onClick;
			ElementBounds textBounds = ElementBounds.Fixed(0.0, 0.0, bounds.fixedWidth, 1.0);
			textBounds.ParentBounds = bounds;
			textElement = new GuiElementRichtext(capi, VtmlUtil.Richtextify(capi, vtmlText, font), textBounds) { zPos = 210.0f };
			bounds.WithChild(textBounds);
		}

		public override void BeforeCalcBounds() { PositionTextElement(recalculateHeight: true); }

		public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
		{
			Bounds.CalcWorldBounds();
			PositionTextElement(recalculateHeight: true);
			ComposeButtonTexture(ref normalTexture, hover: false, pressed: false);
			ComposeButtonTexture(ref hoverTexture, hover: true, pressed: false);
			ComposeButtonTexture(ref activeTexture, hover: false, pressed: true);
			textElement.ComposeElements(ctxStatic, surfaceStatic);
		}

		public override void RenderInteractiveElements(float deltaTime)
		{
			Bounds.CalcWorldBounds();
			PositionTextElement(recalculateHeight: false);

			int textureId = mouseDownOnElement ? activeTexture : (isOver ? hoverTexture : normalTexture);
			api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
			textElement.RenderInteractiveElements(deltaTime);
			MouseOverCursor = null;
		}

		public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
		{
			bool wasOver = isOver;
			isOver = enabled && IsPositionInside(args.X, args.Y);
			if (!wasOver && isOver) { api.Gui.PlaySound("menubutton"); }
		}

		public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
		{
			if (!enabled) return;

			mouseDownOnElement = true;
			args.Handled = true;
			api.Gui.PlaySound("menubutton_down");
		}

		public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
		{
			bool clicked = mouseDownOnElement && enabled && IsPositionInside(args.X, args.Y);
			mouseDownOnElement = false;

			if (clicked)
			{
				api.Gui.PlaySound("menubutton_press");
				args.Handled = onClick();
			}
		}

		public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
		{
			if (!enabled || !HasFocus || args.KeyCode != 49) return;

			api.Gui.PlaySound("menubutton_press");
			args.Handled = onClick();
		}

		private void PositionTextElement(bool recalculateHeight)
		{
			double textWidth = Math.Max(1.0, Bounds.fixedWidth - 12.0);
			textElement.Bounds.fixedX = 6.0;
			textElement.Bounds.fixedWidth = textWidth;
			textElement.MaxHeight = (int)Math.Max(1.0, GuiElement.scaled(Bounds.fixedHeight));
			textElement.Bounds.MarkDirtyRecursive();

			if (recalculateHeight) { textElement.CalcHeightAndPositions(); }

			double textHeight = Math.Min(Bounds.fixedHeight, textElement.Bounds.fixedHeight);
			textElement.Bounds.fixedY = Math.Max(0.0, Math.Floor((Bounds.fixedHeight - textHeight) / 2.0) - (textHeight > 30.0 ? 2.0 : 0.0));
			textElement.Bounds.MarkDirtyRecursive();
			textElement.Bounds.CalcWorldBounds();
		}

		private void ComposeButtonTexture(ref int textureId, bool hover, bool pressed)
		{
			Bounds.CalcWorldBounds();

			int width = Math.Max(1, Bounds.OuterWidthInt);
			int height = Math.Max(1, Bounds.OuterHeightInt);
			ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
			Context ctx = genContext(surface);

			GuiElement.RoundRectangle(ctx, 0.0, 0.0, width, height, GuiElement.scaled(2.0));
			ctx.SetSourceRGBA(0.55, 0.39, 0.19, 0.70);
			ctx.FillPreserve();

			SurfacePattern pattern = GuiElement.getPattern(api, GuiElement.dirtTextureName, doCache: true, 54, PanelTextureScale);
			ctx.SetSource((Pattern)pattern);
			ctx.FillPreserve();

			if (hover)
			{
				ctx.SetSourceRGBA(1.0, 0.88, 0.58, 0.22);
				ctx.FillPreserve();
			}

			if (pressed)
			{
				ctx.SetSourceRGBA(0.06, 0.035, 0.015, 0.34);
				ctx.FillPreserve();
			}

			ctx.SetSourceRGBA(0.19, 0.11, 0.045, hover ? 0.85 : 0.62);
			ctx.LineWidth = GuiElement.scaled(2.0);
			ctx.Stroke();

			ctx.Dispose();
			((Surface)surface).MarkDirty();
			generateTexture(surface, ref textureId);
			((Surface)surface).Dispose();
		}

		public override void Dispose()
		{
			base.Dispose();
			textElement.Dispose();
			if (normalTexture > 0) api.Render.GLDeleteTexture(normalTexture);
			if (hoverTexture > 0) api.Render.GLDeleteTexture(hoverTexture);
			if (activeTexture > 0) api.Render.GLDeleteTexture(activeTexture);
		}
	}

	private struct RpCreatorLayout
	{
		public bool HasImage;
		public ElementBounds ContentPanel;
		public ElementBounds DescPanel;
		public ElementBounds DescClip;
		public ElementBounds DescText;
		public ElementBounds DescScrollbar;
		public ElementBounds ChoicesPanel;
		public ElementBounds ChoicesClip;
		public ElementBounds ChoicesList;
		public ElementBounds ChoicesScrollbar;
		public ElementBounds ImagePanel;
		public ElementBounds ImageContent;
	}
}

#region GUI
public sealed class GuiDialogRpVisualCharacterCreator : GuiDialogCreateCharacter
{
	private readonly Action<RpCharacterCreatorVisualCompletePacket> Completed;

	protected override bool AllowClassSelection => false;

	public override bool DisableMouseGrab => true;
	public override bool OnEscapePressed() => false;

	public GuiDialogRpVisualCharacterCreator(ICoreClientAPI capi, CharacterSystem modSys, Action<RpCharacterCreatorVisualCompletePacket> completed) : base(capi, modSys)
	{
		Completed = completed;
	}

	protected override void OnTitleBarClose() { } // No skipping visual character creation.

	public override void OnGuiClosed()
	{
		bool completed = didSelect;
		RpCharacterCreatorVisualCompletePacket packet = completed ? CreateVisualCompletePacket() : new RpCharacterCreatorVisualCompletePacket { Completed = false };

		if (characterInv != null)
		{
			characterInv.Close(capi.World.Player);

			if (Composers.ContainsKey("createcharacter"))
			{
				GuiComposer composer = Composers["createcharacter"];
				composer?.GetSlotGrid("leftSlots")?.OnGuiClosed(capi);
				composer?.GetSlotGrid("rightSlots")?.OnGuiClosed(capi);
			}
		}

		capi.World.Player.Entity.GetBehavior<EntityBehaviorPlayerInventory>().hideClothing = false;
		reTesselate();

		if (completed) { Completed(packet); }
	}

	private RpCharacterCreatorVisualCompletePacket CreateVisualCompletePacket()
	{
		RpCharacterCreatorVisualCompletePacket packet = new RpCharacterCreatorVisualCompletePacket { Completed = true };

		EntityBehaviorExtraSkinnable? behavior = capi.World.Player.Entity.GetBehavior<EntityBehaviorExtraSkinnable>();
		if (behavior == null) return packet;

		List<RpSkinPartSelection> skinParts = new List<RpSkinPartSelection>();

		foreach (AppliedSkinnablePartVariant part in behavior.AppliedSkinParts)
		{
			skinParts.Add(new RpSkinPartSelection
			{
				PartCode = part.PartCode,
				Code = part.Code
			});
		}

		packet.VoiceType = behavior.VoiceType ?? "";
		packet.VoicePitch = behavior.VoicePitch ?? "";
		packet.SkinParts = skinParts.ToArray();

		return packet;
	}
}
#endregion
