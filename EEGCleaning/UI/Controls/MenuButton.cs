﻿using System.ComponentModel;

namespace EEGCleaning.UI.Controls
{
    public class MenuButton : Button
    {
        [DefaultValue(null)]
        public ContextMenuStrip? Menu { get; set; } = null;

        [DefaultValue(false)]
        public bool ShowMenuUnderCursor { get; set; } = false;

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (Menu != null && mevent.Button == MouseButtons.Left)
            {
                Point menuLocation;

                if (ShowMenuUnderCursor)
                {
                    menuLocation = mevent.Location;
                }
                else
                {
                    menuLocation = new Point(0, Height - 1);
                }

                Menu.Show(this, menuLocation);
            }
            else
            {
                base.OnMouseDown(mevent);
            }
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);

            if (Menu != null)
            {
                int arrowX = ClientRectangle.Width - Padding.Right - 14;
                int arrowY = (ClientRectangle.Height / 2) - 1;

                Color color = Enabled ? ForeColor : SystemColors.ControlDark;
                using (Brush brush = new SolidBrush(color))
                {
                    Point[] arrows = new Point[] { new Point(arrowX, arrowY), new Point(arrowX + 7, arrowY), new Point(arrowX + 3, arrowY + 4) };
                    pevent.Graphics.FillPolygon(brush, arrows);
                }
            }
        }
    }
}
