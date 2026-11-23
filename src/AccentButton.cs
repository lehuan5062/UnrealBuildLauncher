using System.Drawing.Drawing2D;

namespace UELauncher;

/// <summary>A normal button in a color: same visible size and rounded corners as the themed buttons next to it, which leave a 1 px margin inside their bounds.</summary>
public sealed class AccentButton : Button
{
	bool _hot, _down;

	protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
	protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
	protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

	protected override void OnPaint(PaintEventArgs e)
	{
		var G = e.Graphics;
		G.Clear(Parent?.BackColor ?? SystemColors.Control);
		G.SmoothingMode = SmoothingMode.AntiAlias;
		var Fill = !Enabled ? BackColor : _down ? ControlPaint.Dark(BackColor, 0.1f) : _hot ? ControlPaint.Light(BackColor, 0.15f) : BackColor;
		var R = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
		const float D = 6;
		using var Path_ = new GraphicsPath();
		Path_.AddArc(R.X, R.Y, D, D, 180, 90);
		Path_.AddArc(R.Right - D, R.Y, D, D, 270, 90);
		Path_.AddArc(R.Right - D, R.Bottom - D, D, D, 0, 90);
		Path_.AddArc(R.X, R.Bottom - D, D, D, 90, 90);
		Path_.CloseFigure();
		using (var Brush = new SolidBrush(Fill)) { G.FillPath(Brush, Path_); }
		TextRenderer.DrawText(G, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
		if (Focused && ShowFocusCues) { ControlPaint.DrawFocusRectangle(G, Rectangle.Inflate(ClientRectangle, -3, -3), ForeColor, Fill); }
	}
}
