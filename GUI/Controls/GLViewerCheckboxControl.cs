using System.Windows.Forms;

namespace GUI.Controls
{
    public partial class GLViewerCheckboxControl : UserControl
    {
        public CheckBox CheckBox => checkBox;

        public GLViewerCheckboxControl()
        {
            InitializeComponent();
        }

        public GLViewerCheckboxControl(string name, bool isChecked)
            : this()
        {
            checkBox.Text = name;
            checkBox.Checked = isChecked;
        }
    }
}
