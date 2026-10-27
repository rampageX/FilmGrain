using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private void RunLutPreviewUtility(Form owner, string root, string ffdir, Action refresh)
        {
            refresh();if(!Directory.Exists(root))return;
            string output=Path.Combine(root,"_LUT_PREVIEWS");int missing=0;
            try {
                foreach(string lut in NativeLibraryTools.Scan(root,output,new[]{".cube",".3dl",".dat",".m3d",".csp"},delegate{}))
                    if(!File.Exists(NativeLibraryTools.PreviewPath(root,output,lut)))missing++;
            }catch(Exception ex){MessageBox.Show(owner,LF("config.detect.lut_fail",ex.Message));return;}
            if(missing==0)return;
            string tools=Path.Combine(appRoot,"_LUT_Tools"),current=Path.Combine(tools,"LUT_Reference_Current.jpg");
            string reference=File.Exists(current)?current:Path.Combine(tools,"LUT_Reference_Default.jpg");
            string ffmpeg=Path.Combine(ffdir,"ffmpeg.exe");
            if(!File.Exists(reference)||!File.Exists(ffmpeg)){MessageBox.Show(owner,lang.T("config.lut_tool_missing"));return;}
            string label=reference==current?lang.T("config.reference_current"):lang.T("config.reference_default");
            if(MessageBox.Show(owner,LF("config.lut_confirm",label,missing,reference),lang.T("config.lut_create_title"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            NativeLibraryToolDialog.Show(owner,lang.T("config.utility_lut"),delegate(CancellationToken token,Action<string> log){
                return NativeLibraryTools.BuildPreviews(root,output,reference,ffmpeg,false,false,token,log,NativeLibraryTools.RunProcess);
            });refresh();
        }
    }
}
