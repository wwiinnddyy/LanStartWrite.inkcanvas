namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 橡皮的擦法。面积擦对应引擎的点擦（擦除圈覆盖到的那一段被切掉），
/// 笔迹擦对应整笔摘除（命中一笔就整笔没了）。
/// 与 <see cref="PenKind"/> 同样是公开枚举：它出现在公开窗口的成员签名上。
/// </summary>
public enum EraserMode
{
    Area,
    Stroke,
}
