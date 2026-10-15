using System.Drawing;
using System.Windows.Forms;

namespace ClickyWindows.Helpers;

/// <summary>Tray right-click menu in the mac panel colors (background #101211, hover white 8 %).</summary>
internal static class DarkMenu
{
    private static readonly Color Background = Color.FromArgb(0x10, 0x12, 0x11);
    private static readonly Color Hover = Color.FromArgb(0x27, 0x2A, 0x29);
    private static readonly Color Border = Color.FromArgb(0x37, 0x3B, 0x39);
    private static readonly Color Text = Color.FromArgb(0xEC, 0xEE, 0xED);
    private static readonly Color TextDisabled = Color.FromArgb(0x6B, 0x73, 0x6F);

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = Background;
        menu.ShowImageMargin = false;
        menu.Font = new Font("Segoe UI", 9f);
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = item.Enabled ? Text : TextDisabled;
            item.Padding = new Padding(4, 3, 4, 3);
        }
    }

    private sealed class Renderer() : ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDisabled; // keep the disabled header readable
            base.OnRenderItemText(e);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
    }
}
