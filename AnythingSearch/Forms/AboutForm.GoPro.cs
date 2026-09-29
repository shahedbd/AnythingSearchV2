using AnythingSearch.Helper;
using AnythingSearch.Services.Store;

namespace AnythingSearch.Forms;

/// <summary>
/// About dialog's edition badge, next to the version badge: "FREE - Go Pro" opens the Go Pro
/// dialog; "PRO" thanks the owner and opens the "You're Pro" dialog. Kept in its own partial
/// so AboutForm.cs stays within the file-size limit. See 05_Go_Pro_Design.md §8.
/// </summary>
public partial class AboutForm
{
    private static readonly Color ProAccent = ColorIcons.ProStar.Color;

    private void AddEditionBadge(Point location, int height)
    {
        var badge = new Label
        {
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.Black,
            BackColor = ProAccent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(Scale(110), height),
            Location = location,
            Cursor = Cursors.Hand
        };
        UpdateEditionBadge(badge);

        badge.Click += (_, _) =>
        {
            using var dialog = new GoProForm();
            dialog.ShowDialog(this);
            UpdateEditionBadge(badge);   // may have just bought Pro
        };

        this.Controls.Add(badge);
    }

    private static void UpdateEditionBadge(Label badge)
    {
        badge.Text = ProLicenseManager.Instance.IsPro ? "★ PRO" : "FREE · Go Pro";
    }
}
