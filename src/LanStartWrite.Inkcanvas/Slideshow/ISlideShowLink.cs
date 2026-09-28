using System;
using System.Collections.Generic;
using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// <b>一道联动方式</b>：把"正在放映的幻灯片"这件事告诉本应用。
/// </summary>
/// <remarks>
/// <para>
/// <b>它就是那道缝。</b>三档联动的差别在<b>哪一侧</b>：内置那档本进程自己用 COM 连宿主，
/// 另两档是外置插件收好消息再给我们。而不管哪一侧，宿主（PowerPoint / WPS / …）
/// 对本应用只回答一件事：<b>第几页 / 一共几页 / 哪一份文件</b>。
/// 所以接口就留这三样，多一样都不给。
/// </para>
/// <para>
/// <b>刻意不要"翻页事件"，要"去问"。</b>事件看起来更及时，而它有三个做不到的事：
/// COM 侧在 .NET 里接事件要么拿连接点要么轮询（后者其实一样），
/// 而外置插件那两档的事件还多一层跨进程投递。<b>轮询的代价是几拍延迟</b>，
/// 换来的是三种实现写起来一模一样、且断线后不会"事件再也不来"——
/// 那后者的症状是<b>翻页之后我们的页码永远停在上一页</b>，而轮询永远能自愈。
/// </para>
/// <para>
/// <b>为什么值得单独一个接口</b>：真的宿主与 COM 在本机之外没法进验收，
/// 而页数池、左下角控制器、退出、沉浸式摆位全都要能跑通。理由与
/// <c>IDocumentCameraFrames</c> 一字不差。
/// </para>
/// </remarks>
internal interface ISlideShowLink : IDisposable
{
    /// <summary>这一档叫什么（设置页与提示条都念它）。</summary>
    string DisplayName { get; }

    /// <summary>现在能不能用，以及一句人话原因。<b>不抛</b>。</summary>
    SlideShowLinkReport Probe();

    /// <summary>第 <paramref name="index"/> 张（<b>0 基</b>）的缩略图。<b>允许 null</b>。</summary>
    FrameworkElement? Thumbnail(int index);
}

/// <summary>联动方式的元数据 + <b>它自己的设置项</b>。</summary>
/// <remarks>
/// <para>
/// <b>设置项挂在这一档身上，不挂在一处 <c>if</c> 上。</b>
/// 用户换一档，底下那张卡片就要换成那一档的项 —— 而"哪里变了"如果写成
/// <c>switch (mode) { … }</c> 再去改别的控件的 Visibility，那么<b>加一档就要改两个地方</b>，
/// 漏一处就是"新的一档带着旧的一档的项出现"，而界面上完全看不出错。
/// </para>
/// <para>
/// 所以这一档<b>自己说</b>它带哪些项（<see cref="BuildSettings"/>），
/// 而设置页只负责"把返回的那些个建出来并摆进那一格"。
/// </para>
/// </remarks>
/// <param name="Mode">这一档的标识（也是存档里存的那个值）。</param>
/// <param name="Name">设置页下拉里念的名字。</param>
/// <param name="Description">这一档是什么、要不要装别的东西。</param>
/// <param name="Available">眼下是否已实现。false 时选它会说人话，而不是"点了没反应"。</param>
internal sealed record SlideShowLinkDescriptor(
    SlideShowLinkMode Mode,
    string Name,
    string Description,
    bool Available)
{
    /// <summary>
    /// 建出这一档<b>自己的</b>设置项。返回的每一项都会被设置页摆进「放映管理」那一节。
    /// </summary>
    /// <param name="host">建出控件时要用到的宿主（设置页那一格、连回调）。</param>
    /// <param name="mode">当前选中的那一档 —— 建项时常常要按它决定初始值。</param>
    internal IReadOnlyList<FrameworkElement> BuildSettings(SlideShowSettingsHost host, SlideShowLinkMode mode) => [];
}

/// <summary>建设置项时宿主的那些能力。<b>刻意只有一个</b>：那一格容器 + 改档的回调。</summary>
internal sealed class SlideShowSettingsHost
{
    private readonly StackPanel _panel;
    private readonly Action _onModeChanged;

    internal SlideShowSettingsHost(StackPanel panel, Action onModeChanged)
    {
        _panel = panel;
        _onModeChanged = onModeChanged;
    }

    /// <summary>把一项加进「放映管理」那一节的<b>下拉之后</b>。</summary>
    internal void Add(FrameworkElement element) => _panel.Children.Add(element);

    /// <summary>设置页切到某一档时调它，让那一档重新发一次事件。</summary>
    internal void RaiseModeChanged() => _onModeChanged();
}

