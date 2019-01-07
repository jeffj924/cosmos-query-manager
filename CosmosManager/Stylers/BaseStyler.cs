﻿using ScintillaNET;
using System.Drawing;

namespace CosmosManager.Stylers
{
    public class BaseStyler
    {
        public void SetDefaultStyles(Scintilla textbox)
        {
            textbox.StyleResetDefault();
            textbox.Styles[Style.Default].Font = "Consolas";
            textbox.Styles[Style.Default].Size = 11;
            textbox.Styles[Style.Default].BackColor = Color.FromArgb(30, 30, 30);
            textbox.Styles[Style.Default].ForeColor = IntToColor(0xFFFFFF);
            textbox.StyleClearAll();
        }

        protected static Color IntToColor(int rgb)
        {
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        protected void InitColors(Scintilla textbox)
        {
            textbox.CaretForeColor = Color.White;
            textbox.CaretLineVisible = false;
            textbox.SetSelectionBackColor(true, Color.DarkGray);
        }

        protected void InitNumberMargin(Scintilla textbox)
        {

            textbox.Styles[Style.LineNumber].BackColor = Color.FromArgb(51, 51, 51);
            textbox.Styles[Style.LineNumber].ForeColor = IntToColor(0xF1DC01);
            textbox.Styles[Style.IndentGuide].ForeColor = IntToColor(0xF1DC01);
            textbox.Styles[Style.IndentGuide].BackColor = Color.FromArgb(51, 51, 51);

            var nums = textbox.Margins[1];
            nums.Width = 30;
            nums.Type = MarginType.Number;
            nums.Sensitive = true;
            nums.Mask = 0;

        }
    }
}