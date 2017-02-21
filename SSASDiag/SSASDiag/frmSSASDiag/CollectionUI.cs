﻿using Microsoft.AnalysisServices;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.IO;
using System.Windows.Forms;
using System.Data.SqlClient;
using System.Configuration;

namespace SSASDiag
{
    public partial class frmSSASDiag : Form
    {
        #region  CollectionUI

        private void btnCapture_Click(object sender, EventArgs e)
        {
            // worker we use to launch either start or stop blocking operations to the CDiastnosticsCollector asynchronously
            BackgroundWorker bg = new BackgroundWorker();

            if (dc == null || dc.bRunning || !(btnCapture.Image.Tag as string).Contains("Half Lit"))
            {
                if (btnCapture.Image.Tag as string == "Play" || btnCapture.Image.Tag as string == "Play Lit")
                {
                    btnCapture.Click -= btnCapture_Click;
                    btnCapture.Image = imgPlayHalfLit;
                    tbAnalysis.ForeColor = SystemColors.ControlDark;
                    tcCollectionAnalysisTabs.Refresh();
                    txtSaveLocation.Enabled = btnSaveLocation.Enabled = tbAnalysis.Enabled = chkZip.Enabled = chkDeleteRaw.Enabled = groupBox1.Enabled = dtStopTime.Enabled = chkStopTime.Enabled = chkAutoRestart.Enabled = dtStartTime.Enabled = chkRollover.Enabled = chkStartTime.Enabled = udRollover.Enabled = udInterval.Enabled = cbInstances.Enabled = lblInterval.Enabled = lblInterval2.Enabled = false;
                    ComboBoxServiceDetailsItem cbsdi = cbInstances.SelectedItem as ComboBoxServiceDetailsItem;
                    string TracePrefix = Environment.MachineName + (cbsdi == null ? "" : "_"
                        + (cbInstances.SelectedIndex == 0 ? "" : cbsdi.Text + "_"));
                    dc = new CDiagnosticsCollector(TracePrefix, cbInstances.SelectedIndex == 0 || cbsdi == null ? "" : cbsdi.Text, m_instanceVersion, m_instanceType, m_instanceEdition, m_ConfigDir, m_LogDir, (cbsdi == null ? null : cbsdi.ServiceAccount),
                        txtStatus,
                        (int)udInterval.Value, chkAutoRestart.Checked, chkZip.Checked, chkDeleteRaw.Checked, chkProfilerPerfDetails.Checked, chkXMLA.Checked, chkABF.Checked, chkBAK.Checked, (int)udRollover.Value, chkRollover.Checked, dtStartTime.Value, chkStartTime.Checked, dtStopTime.Value, chkStopTime.Checked,
                        chkGetConfigDetails.Checked, chkGetProfiler.Checked, chkGetPerfMon.Checked, chkGetNetwork.Checked);
                    txtStatus.DataBindings.Clear();
                    txtStatus.DataBindings.Add("Lines", dc, "Status", false, DataSourceUpdateMode.OnPropertyChanged);
                    dc.CompletionCallback = callback_StartDiagnosticsComplete;
                    // Unhook the status text area from selection while we are actively using it.
                    // I do allow selection after but it was problematic to scroll correctly while allowing user selection during active collection.
                    // This is functionally good, allows them to copy paths or file names after completion but also gives nice behavior during collection.
                    txtStatus.Cursor = Cursors.Arrow;
                    txtStatus.GotFocus += txtStatus_GotFocusWhileRunning;
                    txtStatus.Enter += txtStatus_EnterWhileRunning;
                    new Thread(new ThreadStart(() => dc.StartDiagnostics())).Start();
                }
                else if (btnCapture.Image.Tag as string == "Stop" || btnCapture.Image.Tag as string == "Stop Lit")
                {
                    {
                        btnCapture.Click -= btnCapture_Click;
                        btnCapture.Image = imgStopHalfLit;
                        new Thread(new ThreadStart(() => dc.StopAndFinalizeAllDiagnostics())).Start();
                    }
                }
            }
        }

        private void tcCollectionAnalysisTabs_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabPage page = tcCollectionAnalysisTabs.TabPages[e.Index];
            e.Graphics.FillRectangle(new SolidBrush(page.BackColor), e.Bounds);
            Rectangle paddedBounds = e.Bounds;
            int yOffset = (e.State == DrawItemState.Selected) ? -2 : 1;
            paddedBounds.Offset(1, yOffset);
            TextRenderer.DrawText(e.Graphics, page.Text, Font, paddedBounds, page.ForeColor);
        }


        #region BlockingUIComponentsBesidesCapture
        class ComboBoxServiceDetailsItem
        {
            public string Text { get; set; }
            public string ConfigPath { get; set; }
            public string ServiceAccount { get; set; }
        }
        private void cbInstances_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnCapture.Enabled = false;
            new Thread(new ThreadStart(() =>
            {
                try
                {
                    Server srv = new Server();
                    ComboBoxServiceDetailsItem SelItem = cbInstances.Invoke(new Func<ComboBoxServiceDetailsItem>(() => { return (cbInstances.SelectedItem as ComboBoxServiceDetailsItem); })) as ComboBoxServiceDetailsItem;

                    srv.Connect("Data source=" + Environment.MachineName + (SelItem.Text == "Default instance (MSSQLServer)" ? "" : "\\" + SelItem.Text) + ";Integrated Security=SSPI;Persist Security Info=false;");
                    lblInstanceDetails.Invoke(new System.Action(() => lblInstanceDetails.Text = "Instance Details:\r\n" + srv.Version + " (" + srv.ProductLevel + "), " + srv.ServerMode + ", " + srv.Edition));
                    m_instanceType = srv.ServerMode.ToString();
                    m_instanceVersion = srv.Version + " - " + srv.ProductLevel;
                    m_instanceEdition = srv.Edition.ToString();
                    m_LogDir = srv.ServerProperties["LogDir"].Value;
                    m_ConfigDir = SelItem.ConfigPath;
                    srv.Disconnect();
                    btnCapture.Invoke(new System.Action(() => btnCapture.Enabled = true));
                }
                catch (Exception ex)
                {
                    if (!lblInstanceDetails.IsDisposed) lblInstanceDetails.Invoke(new System.Action(() => lblInstanceDetails.Text = "Instance details could not be obtained due to failure connecting:\r\n" + ex.Message));
                }
            })).Start();
        }
        private void PopulateInstanceDropdown()
        {
            BackgroundWorker bg = new BackgroundWorker();
            bg.DoWork += bgPopulateInstanceDropdown;
            bg.RunWorkerCompleted += bgPopulateInstanceDropdownComplete;
            bg.RunWorkerAsync();
        }
        private void bgPopulateInstanceDropdown(object sender, DoWorkEventArgs e)
        {
            try
            {
                ServiceController[] services = ServiceController.GetServices();
                foreach (ServiceController s in services.OrderBy(ob => ob.DisplayName))
                    if (s.DisplayName.Contains("Analysis Services") && !s.DisplayName.Contains("SQL Server Analysis Services CEIP ("))
                    {
                        SelectQuery sQuery = new SelectQuery("select name, startname, pathname from Win32_Service where name = \"" + s.ServiceName + "\"");
                        ManagementObjectSearcher mgmtSearcher = new ManagementObjectSearcher(sQuery);
                        string sSvcUser = "";
                        foreach (ManagementObject svc in mgmtSearcher.Get())
                            sSvcUser = svc["startname"] as string;
                        if (sSvcUser.Contains(".")) sSvcUser = sSvcUser.Replace(".", Environment.UserDomainName);
                        if (sSvcUser == "LocalSystem") sSvcUser = "NT AUTHORITY\\SYSTEM";

                        string ConfigPath = Registry.LocalMachine.OpenSubKey("SYSTEM\\ControlSet001\\Services\\" + s.ServiceName, false).GetValue("ImagePath") as string;
                        ConfigPath = ConfigPath.Substring(ConfigPath.IndexOf("-s \"") + "-s \"".Length).TrimEnd('\"');
                        if (s.DisplayName.Replace("SQL Server Analysis Services (", "").Replace(")", "").ToUpper() == "MSSQLSERVER")
                            LocalInstances.Insert(0, new ComboBoxServiceDetailsItem() { Text = "Default instance (MSSQLServer)", ConfigPath = ConfigPath, ServiceAccount = sSvcUser });
                        else
                            LocalInstances.Add(new ComboBoxServiceDetailsItem() { Text = s.DisplayName.Replace("SQL Server Analysis Services (", "").Replace(")", ""), ConfigPath = ConfigPath, ServiceAccount = sSvcUser });
                    }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Failure during instance enumeration - could be because no instances were there.  Move on quietly then.");
                System.Diagnostics.Trace.WriteLine(ex);
            }
            if (LocalInstances.Count == 0)
                cbInstances.Invoke(new System.Action(() => cbInstances.Enabled = false));
        }
        private void bgPopulateInstanceDropdownComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            cbInstances.DataSource = LocalInstances;
            cbInstances.DisplayMember = "Text";
            cbInstances.Refresh();
            if (cbInstances.Items.Count > 0) cbInstances.SelectedIndex = 0;
            if (LocalInstances.Count == 0)
                lblInstanceDetails.Text = "There were no Analysis Services instances found on this server.\r\nPlease run on a server with a SQL 2008 or later SSAS instance.";
        }
        #endregion BlockingUIComponentsBesidesCapture

        #region VariousNonBlockingUIElements

        private void btnSaveLocation_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.ShowNewFolderButton = true;
            fbd.Description = "Select save location for capture.";
            fbd.SelectedPath = txtSaveLocation.Text;
            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                Properties.Settings.Default["SaveLocation"] = Environment.CurrentDirectory = txtSaveLocation.Text = fbd.SelectedPath;
                Properties.Settings.Default.Save();
            }
        }
        private void txtStatus_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                string sOut = "";
                foreach (string s in txtStatus.Lines)
                    sOut += s + "\r\n";
                Clipboard.SetData(DataFormats.StringFormat, sOut);
                ttStatus.Show("Output window text copied to clipboard.", txtStatus, 2500);
                new Thread(new ThreadStart(new System.Action(() =>
                {
                    Thread.Sleep(2500);
                    txtStatus.Invoke(new System.Action(() => ttStatus.SetToolTip(txtStatus, "")));
                }))).Start();
            }
        }
        private void btnCapture_MouseEnter(object sender, EventArgs e)
        {
            if (btnCapture.Image.Tag as string == "Play")
                btnCapture.Image = imgPlayLit;
            else if (btnCapture.Image.Tag as string == "Stop")
                btnCapture.Image = imgStopLit;
        }
        private void btnCapture_MouseLeave(object sender, EventArgs e)
        {
            if (btnCapture.Image.Tag as string == "Play Lit")
                btnCapture.Image = imgPlay;
            else if (btnCapture.Image.Tag as string == "Stop Lit")
                btnCapture.Image = imgStop;
        }

        #region CaptureDetailsUI
        private void chkRollover_CheckedChanged(object sender, EventArgs e)
        {
            if (chkRollover.Checked) udRollover.Enabled = true; else udRollover.Enabled = false;
            if (chkGetNetwork.Checked && chkRollover.Checked)
                ttStatus.Show("NOTE: Network traces rollover circularly,\n"
                            + "always deleting older data automatically.", chkRollover, 3500);
        }
        private void chkStopTime_CheckedChanged(object sender, EventArgs e)
        {
            dtStopTime.Enabled = chkStopTime.Checked;
            if (!chkStopTime.Checked)
            {
                if (chkAutoRestart.Checked) ttStatus.Show("AutoRestart disabled for your protection without stop time.", chkAutoRestart, 1750);
                chkAutoRestart.Checked = false;
            }
            else
                dtStopTime.Value = DateTime.Now.AddHours(1);
        }
        private void chkStartTime_CheckedChanged(object sender, EventArgs e)
        {
            dtStartTime.Enabled = chkStartTime.Checked;
            if (chkStartTime.Checked) dtStartTime.Value = DateTime.Now.AddHours(0);
        }
        private void chkAutoRestart_CheckedChanged(object sender, EventArgs e)
        {
            if (chkAutoRestart.Checked && !chkStopTime.Checked)
            {
                ttStatus.Show("Stop time required for your protection with AutoRestart=true.", dtStopTime, 1750);
                chkStopTime.Checked = true;
            }
        }
        private void chkZip_CheckedChanged(object sender, EventArgs e)
        {
            if (!chkZip.Checked)
                chkDeleteRaw.Checked = false;
        }
        private void chkDeleteRaw_CheckedChanged(object sender, EventArgs e)
        {
            if (chkDeleteRaw.Checked)
            {
                ttStatus.Show("After zip, keep raw data for analysis.\r\nThis saves the zip decompression step later.", chkDeleteRaw, 4000);
                chkZip.Checked = true;
            }
        }
        private void SetRolloverAndStartStopEnabledStates()
        {
            chkRollover.Enabled = chkStartTime.Enabled = chkStopTime.Enabled = dtStartTime.Enabled = dtStopTime.Enabled
                = chkGetPerfMon.Checked | chkGetProfiler.Checked | chkGetNetwork.Checked;
            udRollover.Enabled = chkRollover.Enabled & chkRollover.Checked;
            dtStartTime.Enabled = chkStartTime.Enabled & chkStartTime.Checked;
            dtStopTime.Enabled = chkStopTime.Enabled & chkStopTime.Checked;
        }
        #endregion CaptureDetailsUI

        #region DiagnosticsToCaptureUI

        #region SimpleDiagnosticsUI
        private void cmbProblemType_SelectedIndexChanged(object sender, EventArgs e)
        {
            chkGetConfigDetails.Checked = chkGetNetwork.Checked = chkGetPerfMon.Checked = chkGetProfiler.Checked = chkProfilerPerfDetails.Checked = false;

            switch (cmbProblemType.SelectedItem as string)
            {
                case "Performance":
                    rtbProblemDescription.Height = 170;
                    rtbProblemDescription.Text = "Performance issues require minimal collection of config details, performance monitor logs, and extended profiler traces including performance relevant details.\r\n\r\n"
                                           + "Including AS backups can allow further investigation to review data structures, rerun problematic queries, or test changes to calculations.\r\n\r\n"
                                           + "Including SQL data source backups can further allow experimental changes and full reprocessing of data structures.";
                    chkProfilerPerfDetails.Checked = chkGetProfiler.Checked = chkGetPerfMon.Checked = chkXMLA.Checked = chkGetConfigDetails.Checked = true;
                    break;
                case "Errors/Hangs (non-connectivity)":
                    rtbProblemDescription.Height = 170;
                    rtbProblemDescription.Text = "Non-connectivity related errors require minimal collection of config details, performance monitor logs, and basic profiler traces.\r\n\r\n"
                                           + "Including AS backups can allow further investigation to review data structures, rerun problematic queries, or test changes to calculations.\r\n\r\n"
                                           + "Including SQL data source backups can further allow experimental changes and full reprocessing of data structures.";
                    chkGetPerfMon.Checked = chkGetProfiler.Checked = chkGetConfigDetails.Checked = true;
                    break;
                case "Connectivity Failures":
                    rtbProblemDescription.Height = 287;
                    rtbProblemDescription.Text = "Connectivity failures require minimal collection of config details, performance monitor logs, basic profiler traces, and network traces.\r\n\r\n"
                                           + "Network traces should be captured on a failing client, and any middle tier server, for multi-tier scenarios.\r\n\r\n"
                                           + "Service Principle Names registered in Active Directory are captured if the tool is run as a domain administrator.\r\n\r\n"
                                           + "Including AS backups can allow further investigation to review data structures, rerun problematic queries, or test changes to calculations.\r\n\r\n"
                                           + "Including SQL data source backups can further allow experimental changes and full reprocessing of data structures.";
                    chkGetConfigDetails.Checked = chkGetProfiler.Checked = chkGetPerfMon.Checked = chkGetNetwork.Checked = true;
                    break;
                case "Connectivity (client/middle-tier only)":
                    rtbProblemDescription.Height = 130;
                    rtbProblemDescription.Text = "Connectivity failures on client and middle tier require collection of network traces.\r\n\r\n"
                                           + "Network traces should be captured on a failing client, and any middle tier server, for multi-tier scenarios.\r\n\r\n"
                                           + "Service Principle Names registered in Active Directory are captured if the tool is run as a domain administrator.";
                    chkXMLA.Checked = chkABF.Checked = chkBAK.Checked = false;
                    chkGetNetwork.Checked = true;
                    break;
                case "Incorrect Query Results":
                    rtbProblemDescription.Height = 170;
                    rtbProblemDescription.Text = "Incorrect results require minimal collection of config details and basic profiler traces, as well as full SQL data source backups.\r\n\r\n"
                                           + "Including AS backups can allow further investigation to review data structures, rerun problematic queries, or test changes to calculations.\r\n\r\n"
                                           + "Including SQL data source backups allows all experimental changes and full reprocessing of data structures.";
                    chkGetConfigDetails.Checked = chkGetProfiler.Checked = true;
                    tbLevelOfData.Value = 2;
                    ttStatus.Show("Including SQL data source backups can increase data collection size and time required to stop collection.", tbLevelOfData, 1500);
                    break;
                case "Data Corruption":
                    rtbProblemDescription.Height = 210;
                    rtbProblemDescription.Text = "Data corruption issues require minimal collection of config details (including Application and System Event logs), performance monitor logs, basic profiler traces, and AS backups.\r\n\r\n"
                                           + "Including AS backups allows investigation to review corrupt data, in some cases allowing partial or full recovery.\r\n\r\n"
                                           + "Including SQL data source backups can further allow experimental changes and full reprocessing of data structures.";
                    chkGetConfigDetails.Checked = chkGetProfiler.Checked = chkGetPerfMon.Checked = true;
                    if (tbLevelOfData.Value != 1)
                    {
                        tbLevelOfData.Value = 1;
                        ProcessSliderMiddlePosition();
                    }
                    ttStatus.Show("Including AS backups can increase data collection size and time required to stop collection.", tbLevelOfData, 1500);
                    break;
            }
        }
        private void tbLevelOfData_ValueChanged(object sender, EventArgs e)
        {
            chkABF.Checked = chkBAK.Checked = chkXMLA.Checked = false;
            lblBAK.ForeColor = lblABF.ForeColor = lblXMLA.ForeColor = SystemColors.ControlDark;
            switch (tbLevelOfData.Value)
            {
                case 0:
                    lblXMLA.ForeColor = SystemColors.ControlText;
                    chkXMLA.Checked = true;
                    ttStatus.SetToolTip(tbLevelOfData, null);
                    break;
                case 1:
                    lblABF.ForeColor = Color.Red;
                    chkABF.Checked = true;
                    ttStatus.SetToolTip(tbLevelOfData, "Including AS backups can increase data collection size and time required to stop collection.");
                    break;
                case 2:
                    lblBAK.ForeColor = Color.Red;
                    chkXMLA.Checked = true;
                    chkBAK.Checked = true;
                    ttStatus.SetToolTip(tbLevelOfData, "Including SQL data source backups can increase data collection size and time required to stop collection.");
                    break;
            }
        }
        private void tbLevelOfData_Scroll(object sender, EventArgs e)
        {
            dtLastScrollTime = DateTime.Now;
            tmScrollStart.Start();
        }
        private void tmLevelOfDataScroll_Tick(object sender, EventArgs e)
        {
            TimeSpan ts = DateTime.Now - dtLastScrollTime;
            if (ts.TotalMilliseconds > 250 && tbLevelOfData.Value == 1)
            {
                tmScrollStart.Stop();
                ProcessSliderMiddlePosition();
            }
        }
        private void ProcessSliderMiddlePosition()
        {
            if (chkABF.Checked)
            {
                tmScrollStart.Stop();
                chkGetProfiler.Checked = true;
                string baseMsg = "AS .abf backups provide data to execute queries and obtain results, and allow modification of calculation definitions, but not changes "
                                + "to data definitions requiring reprocessing.  They are the second most optimal dataset to reproduce and investigate issues.\r\n\r\n"
                                + "However, please note that including database or data source backups may siginificantly increase size of data collected and time required to stop collection.";
                if (chkXMLA.Checked)
                    MessageBox.Show("AS backups include database definitions.\nDatabase definitions will be unchecked after you click OK.\r\n\r\n"
                                    + baseMsg,
                                  "Backup Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show(baseMsg, "Backup Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                chkXMLA.Checked = false;
            }
        }
        #endregion SimpleDiagnosticsUI

        #region AdvandedDiagnosticsUI
        private void chkGetConfigDetails_CheckedChanged(object sender, EventArgs e)
        {
            EnsureSomethingToCapture();
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkGetPerfMon_CheckedChanged(object sender, EventArgs e)
        {
            lblInterval.Enabled = udInterval.Enabled = lblInterval2.Enabled = chkGetPerfMon.Checked;
            SetRolloverAndStartStopEnabledStates();
            EnsureSomethingToCapture();
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkProfilerPerfDetails_CheckedChanged(object sender, EventArgs e)
        {
            if (chkProfilerPerfDetails.Checked)
            {
                chkGetProfiler.Checked = true;
                if (MessageBox.Show("Adding verbose performance details to profiler traces accumulates data much more quickly than without, and is often not required even to understand many performance issues.\r\n\r\nDo you want to enable verbose tracing anyway?", "Verbose Trace Details Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.No)
                    chkProfilerPerfDetails.Checked = false;
            }
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkXMLA_CheckedChanged(object sender, EventArgs e)
        {
            if (chkXMLA.Checked)
            {
                if (chkABF.Checked)
                    chkABF.Checked = false;
                chkGetProfiler.Checked = true;
            }
            else
                chkBAK.Checked = false;
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkABF_CheckedChanged(object sender, EventArgs e)
        {
            if (chkABF.Checked)
            {
                chkGetProfiler.Checked = true;
                if (tcSimpleAdvanced.SelectedIndex == 1)
                {
                    string baseMsg = "AS .abf backups provide data to execute queries and obtain results, and allow modification of calculation definitions, but not changes "
                                + "to data definitions requiring reprocessing.  They are the second most optimal dataset to reproduce and investigate issues.\r\n\r\n"
                                + "However, please note that including database or data source backups may siginificantly increase size of data collected and time required to stop collection.";
                    if (chkXMLA.Checked)
                        MessageBox.Show("AS backups include database definitions.\nDatabase definitions will be unchecked after you click OK.\r\n\r\n"
                                        + baseMsg,
                                      "Backup Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else
                        MessageBox.Show(baseMsg, "Backup Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                chkXMLA.Checked = false;
                chkBAK.Checked = false;
            }
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkBAK_CheckedChanged(object sender, EventArgs e)
        {
            if (chkBAK.Checked)
            {
                chkGetProfiler.Checked = chkXMLA.Checked = true;
                MessageBox.Show("AS database definitions with SQL data source backups provide the optimal dataset to reproduce and investigate any issue.\r\n"
                    + "\r\nHowever, please note that including database or data source backups may significantly increase size of data collected and time required to stop collection.",
                    "Backup Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateUIIfOnlyNetworkingEnabled();
        }
        private void chkGetProfiler_CheckedChanged(object sender, EventArgs e)
        {
            chkAutoRestart.Enabled = chkGetProfiler.Checked;
            SetRolloverAndStartStopEnabledStates();
            if (!chkGetProfiler.Checked)
            {
                chkProfilerPerfDetails.Checked = false;
                chkABF.Checked = false;
                chkBAK.Checked = false;
                chkXMLA.Checked = false;
            }
            UpdateUIIfOnlyNetworkingEnabled();
            EnsureSomethingToCapture();
        }
        private void chkGetNetwork_CheckedChanged(object sender, EventArgs e)
        {
            SetRolloverAndStartStopEnabledStates();
            if (chkGetNetwork.Checked && chkRollover.Checked)
                ttStatus.Show("NOTE: Network traces rollover circularly,\n"
                            + "always deleting older data automatically.", chkGetNetwork, 2000);
            if (chkGetNetwork.Checked)
                MessageBox.Show("Please note that including network traces may significantly increase size of data collected and time required to stop collection.",
                    "Network Trace Collection Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateUIIfOnlyNetworkingEnabled();
            EnsureSomethingToCapture();
        }
        private void tcCollectionAnalysisTabs_Selecting(object sender, TabControlCancelEventArgs e)
        {
            if (!tbAnalysis.Enabled)
                e.Cancel = true;
        }
        private void UpdateUIIfOnlyNetworkingEnabled()
        {
            if (chkGetNetwork.Checked && !chkGetProfiler.Checked && !chkGetPerfMon.Checked && !chkGetConfigDetails.Checked && !chkXMLA.Checked && !chkABF.Checked && !chkBAK.Checked)
            {
                lblLevelOfReproData.ForeColor = lblABF.ForeColor = lblBAK.ForeColor = lblXMLA.ForeColor = SystemColors.ControlDark;
                cbInstances.Enabled = tbLevelOfData.Enabled = false;
            }
            else
            {
                lblXMLA.ForeColor = tbLevelOfData.Value == 0 ? SystemColors.ControlText : SystemColors.ControlDarkDark;
                lblABF.ForeColor = tbLevelOfData.Value == 1 ? Color.Red : SystemColors.ControlDarkDark;
                lblBAK.ForeColor = tbLevelOfData.Value == 2 ? Color.Red : SystemColors.ControlDarkDark;
                lblLevelOfReproData.ForeColor = SystemColors.ControlText;
                tbLevelOfData.Enabled = true;
                if (cbInstances.Items.Count > 0) cbInstances.Enabled = true;
            }
        }
        private void EnsureSomethingToCapture()
        {
            btnCapture.Enabled = false;
            if (chkGetConfigDetails.Checked || chkGetPerfMon.Checked || chkGetProfiler.Checked)
            {
                btnCapture.Enabled = true;
                cbInstances_SelectedIndexChanged(null, null);
            }
            else
                if (chkGetNetwork.Checked)
                btnCapture.Enabled = true;
        }

        #endregion AdvandedDiagnosticsUI

        #endregion DiagnosticsToCaptureUI

        #endregion VariousNonBlockingUIElements

        #endregion CollectionUI
    }
}