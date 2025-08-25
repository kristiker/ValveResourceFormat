using System.IO;
using System.Windows.Forms;
using DemoFile;
using Google.Protobuf;
using GUI.Controls;
using GUI.Utils;

namespace GUI.Types.Viewers
{
    class Demo : IViewer
    {
        public static bool IsAccepted(uint magic)
        {
            // 'PBDEMS2' (Protocol Buffer Demo Source 2)
            return magic == IViewer.FourCC('P', 'B', 'D', 'E');
        }

        public TabPage Create(VrfGuiContext vrfGuiContext, Stream stream)
        {
            var tab = new TabPage();
            Stream demoStream;

            var control = CodeTextBox.Create("Reading demo...\n", CodeTextBox.HighlightLanguage.JS);
            tab.Controls.Add(control);

            if (stream != null)
            {
                demoStream = stream;
            }
            else
            {
                demoStream = File.OpenRead(vrfGuiContext.FileName!);
            }

            var demoLog = new CsDemoParser();

            void StreamLine(string line)
            {
                if (control.IsHandleCreated && !control.Disposing && !control.IsDisposed)
                {
                    control.BeginInvoke(() => control.Text += line + '\n');
                }
            }

            var jsonFormatterPb = new JsonFormatter(new JsonFormatter.Settings(true).WithIndentation("\t"));
            var jsonFormatterSettings = new System.Text.Json.JsonSerializerOptions
            {
                ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            };

            demoLog.PacketEvents.SvcServerInfo += e =>
            {
                StreamLine(jsonFormatterPb.Format(e));
            };

            demoLog.Source1GameEvents.Source1GameEvent += e =>
            {
                try
                {
                    var eventJson = System.Text.Json.JsonSerializer.Serialize(e, jsonFormatterSettings);
                    StreamLine(eventJson);
                }
                catch (NotSupportedException)
                {
                    // StreamLine(e.GameEventName);
                }
            };


            var reader = DemoFileReader.Create(demoLog, demoStream);
            var readerTask = reader.ReadAllAsync().AsTask();
            readerTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    StreamLine(t.Exception?.ToString() ?? "Unknown error");
                }

                demoStream.Close();
            }).ConfigureAwait(true);

            return tab;
        }
    }
}
