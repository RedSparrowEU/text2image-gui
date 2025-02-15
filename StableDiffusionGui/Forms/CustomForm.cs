﻿using StableDiffusionGui.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace StableDiffusionGui.Forms
{
    public class CustomForm : Form
    {
        public Control FocusedControl { get { return this.GetControls().Where(c => c.Focused).FirstOrDefault(); } }

        public bool AllowTextboxTab { get; set; } = true;
        public bool AllowEscClose { get; set; } = true;

        private List<Control> _tabOrderedControls;

        public void TabOrderInit(List<Control> tabOrderedControls, bool focusFirst = true)
        {
            _tabOrderedControls = tabOrderedControls;
            this.GetControls().ForEach(control => control.TabStop = false);

            if (focusFirst && tabOrderedControls != null && tabOrderedControls.Count > 0)
                tabOrderedControls[0].Focus();
        }

        public void TabOrderNext()
        {
            if (_tabOrderedControls == null || _tabOrderedControls.Count <= 0)
                return;

            var focused = FocusedControl;

            if (_tabOrderedControls.Contains(focused))
            {
                int index = _tabOrderedControls.IndexOf(focused);
                Control next = null;

                while (_tabOrderedControls.Where(x => x.Visible && x.Enabled).Any() && (next == null || !next.Visible || !next.Enabled))
                {
                    index++;
                    next = _tabOrderedControls.ElementAt(index >= _tabOrderedControls.Count ? 0 : index);
                }

                if (next != null)
                    next.Focus();

                return;
            }

            _tabOrderedControls.First().Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape && AllowEscClose)
                Close();

            if (e.KeyCode == Keys.Tab && !(FocusedControl is TextBox && AllowTextboxTab))
                TabOrderNext();
        }
    }
}
