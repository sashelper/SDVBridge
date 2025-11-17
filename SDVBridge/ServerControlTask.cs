using System.Windows.Forms;
using SAS.Shared.AddIns;
using SAS.Tasks.Toolkit;

namespace SDVBridge
{
    [ClassId("a50dd0f4-2181-4f16-84b1-4b0d37f34298")]
    [Version(8.2)]
    [InputRequired(InputResourceType.None)]
    public class ServerControlTask : SasTask
    {
        public ServerControlTask()
        {
            TaskCategory = "Helper Software";
            TaskName = "SDVBridge";
            TaskDescription = "Start or stop the SDVBridge REST API.";
            RequiresData = false;
            GeneratesReportOutput = false;
            GeneratesSasCode = false;
        }

        public override ShowResult Show(IWin32Window owner)
        {
            var form = new ServerControlForm
            {
                Consumer = Consumer
            };

            form.ShowDialog(owner);
            return ShowResult.Canceled;
        }
    }
}
