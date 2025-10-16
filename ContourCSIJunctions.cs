using System;
using System.Linq;
using System.Text;
using System.Windows;
using WinForms = System.Windows.Forms;
using System.Windows.Input;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using System.Runtime.CompilerServices;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using Image = VMS.TPS.Common.Model.API.Image;
using System.Windows.Media.Media3D;

// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyFileVersion("1.0.0")]
[assembly: AssemblyInformationalVersion("1.0")]

// Script needs write access
[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {

        }
        // I AM WORKING IN DROP DOWNCONTOUR VERIOSN
        // variable initialization
        private Patient _patient;
        private Course _course;
        private StructureSet _ss;
        private PlanSetup _plan;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context/*, ScriptEnvironment environment*/)
        {
            // Validate context
            if (!ValidatePatient(context) ||
                !ValidateCourse(context) ||
                !ValidateStructureSet(context) ||
                !ValidatePlan(context))
            {
                return; // exit 
            }

            try
            {
                _patient = context.Patient;
                _ss = context.StructureSet;
                _course = context.Course;
                _plan = context.PlanSetup;

                // Show the UI to select the contour
                string selectedContourID = ShowContourSelectionWindow(_ss, _patient);

                if (string.IsNullOrEmpty(selectedContourID))
                {
                    MessageBox.Show("No contour selected. Exiting.");
                    return;
                }

                // check for a structure named PTV_CSI
                var ptv = _ss.Structures.FirstOrDefault(s => s.Id.Equals(selectedContourID, StringComparison.OrdinalIgnoreCase));
                if (ptv == null)
                {
                    MessageBox.Show("PTV_CSI structure not found.");
                    return;
                }
                // group beams by iso
                var beamGroups = _plan.Beams.Where(b => !b.IsSetupField).GroupBy(b => new { b.IsocenterPosition.x, b.IsocenterPosition.y, b.IsocenterPosition.z }).ToList();

                if (beamGroups.Count < 2)
                {
                    MessageBox.Show("Plan has only one isocenter - no junction region.");
                    return;
                }

                int junctionCount = beamGroups.Count - 1;
                MessageBox.Show(
                    $"You are working with patient {_patient.Id}, course {_course.Id}, structure set {_ss.Id} and plan {_plan.Id}.\n\n" +
                    $"Found {beamGroups.Count} isocenter groups in the plan, will create {junctionCount} junction structure(s).");

                try
                {
                    //var body = _ss.Structures.FirstOrDefault(d => d.DicomType == "EXTERNAL"); may not need

                    // get the Z span of the CT image
                    var image = context.Image;
                    double[] zValues = image.ZSize > 0
                        ? Enumerable.Range(0, image.ZSize)
                            .Select(i => image.Origin.z + i * image.ZRes)
                            .ToArray()
                        : Array.Empty<double>();


                    _patient.BeginModifications();

                    // generate an isocenter structure (PTV_CSI clipped by min and max Z coordinates)
                    for (int i = 0; i < beamGroups.Count; i++)
                    {
                        var iso = beamGroups[i];

                        // find min and max z
                        double minZ = iso.Min(b => b.ControlPoints.Min(cp => b.IsocenterPosition.z + Math.Min(cp.JawPositions.Y1, cp.JawPositions.Y2))); // distance
                        int minSlice = (int)Math.Round((minZ - image.Origin.z) / image.ZRes); // slice number
                        double maxZ = iso.Max(b => b.ControlPoints.Max(cp => b.IsocenterPosition.z + Math.Max(cp.JawPositions.Y1, cp.JawPositions.Y2)));
                        int maxSlice = (int)Math.Round((maxZ - image.Origin.z) / image.ZRes);

                        //MessageBox.Show($"min Z is: {minZ}\n" +
                        //    $"min Z slice is: {minSlice}\n\n" +
                        //    $"max Z is: {maxZ}\n" +
                        //    $"max Z slice is: {maxSlice}");

                        string isoStructId = $"PTV_ISO_{beamGroups.Count - i}";
                        if (_ss.Structures.Any(s => s.Id.Equals(isoStructId)))
                        {
                            MessageBox.Show($"{isoStructId} exists. Skipping");
                            continue;
                        }

                        Structure isoStruct = _ss.AddStructure("CONTROL", isoStructId);

                        for (int z = minSlice; z <= maxSlice; z++)
                        {
                            var contours = ptv.GetContoursOnImagePlane(z);
                            foreach (var contour in contours)
                            {
                                isoStruct.AddContourOnImagePlane(contour, z);
                            }
                        }
                    }
                    
                    // Contour junctions between isocenter contours
                    for (int j = 0; j < beamGroups.Count - 1; j++) // if beamCount is 3, j will be 1 and 2
                    {
                        string structIdA = $"PTV_ISO_{beamGroups.Count - j}";
                        string structIdB = $"PTV_ISO_{beamGroups.Count - (j + 1)}";
                        string juncStructId = $"JUNC_{beamGroups.Count - (j + 1)}";

                        Structure structA = _ss.Structures.FirstOrDefault(s => s.Id.Equals(structIdA));
                        Structure structB = _ss.Structures.FirstOrDefault(s => s.Id.Equals(structIdB));

                        // for debugging
                        //MessageBox.Show($"j index: {j}\n" +
                        //    $"structIdA: {structIdA}\n" +
                        //    $"structIdB: {structIdB}\n" +
                        //    $"juncStructId: {juncStructId}");

                        if (structA == null || structB == null)
                            continue;

                        // for debugging    
                        //MessageBox.Show("Existing structures:\n" + string.Join("\n", _ss.Structures.Select(s => s.Id)));

                        if (_ss.Structures.Any(s => s.Id.Equals(juncStructId)))
                        {
                            MessageBox.Show($"{juncStructId} exists. Skipping.");
                            continue;
                        }

                        Structure juncStruct = _ss.AddStructure("CONTROL", juncStructId);

                        int minSliceA = (int)Math.Round((structA.MeshGeometry.Bounds.Z - image.Origin.z) / image.ZRes);
                        int maxSliceA = (int)Math.Round(((structA.MeshGeometry.Bounds.Z + structA.MeshGeometry.Bounds.SizeZ) - image.Origin.z) / image.ZRes);

                        int minSliceB = (int)Math.Round((structB.MeshGeometry.Bounds.Z - image.Origin.z) / image.ZRes);
                        int maxSliceB = (int)Math.Round(((structB.MeshGeometry.Bounds.Z + structB.MeshGeometry.Bounds.SizeZ) - image.Origin.z) / image.ZRes);

                        int overlapMinSlice = Math.Max(minSliceA, minSliceB);
                        int overlapMaxSlice = Math.Min(maxSliceA, maxSliceB); // test

                        if (overlapMinSlice > overlapMaxSlice)
                            continue;

                        for (int z = overlapMinSlice; z <= overlapMaxSlice; z++)
                        {
                            var contoursA = structA.GetContoursOnImagePlane(z);
                            var contoursB = structB.GetContoursOnImagePlane(z);

                            if (contoursA.Any() && contoursB.Any())
                            {
                                foreach (var contour in contoursA.Concat(contoursB))
                                {
                                    juncStruct.AddContourOnImagePlane(contour, z);
                                }
                            }
                        }

                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Sorry, something went wrong.\n\n" +
                        $"{ex.Message}\n\n{ex.StackTrace}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Sorry, something went wrong.\n\n" +
                    $"{ex.Message}\n\n{ex.StackTrace}");
                throw;
            }
        }

        //HELPER FUNCTIONS
        #region UI HELPER
        // ========================
        // UI HELPER
        // ========================
        private string ShowContourSelectionWindow(StructureSet _ss, Patient _patient)
        {
            string selectedId = null;

            Window window = new Window();

            StackPanel spMain = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10),
                Width = 400
            };

            TextBlock titleBlock = new TextBlock
            {
                Text = "Junction Creator - Contour Selection",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.MediumBlue,
                Margin = new Thickness(0, 0, 0, 10)
            };

            TextBlock infoBlock = new TextBlock
            {
                Text = $"Patient: {_patient.Name}\nStructure Set: {_ss.Id}",
                Margin = new Thickness(0, 5, 0, 10)
            };

            ComboBox contourComboBox = new ComboBox
            {
                Width = 250,
                Margin = new Thickness(0, 5, 0, 10)
            };

            // Populate dropdown with available structure IDs (only target-like)
            foreach (var s in _ss.Structures
                                 .Where(s => s.DicomType == "PTV" || s.DicomType == "CTV" || s.DicomType == "GTV")
                                 .OrderBy(s => s.Id))
            {
                contourComboBox.Items.Add(s.Id);
            }

            Button okButton = new Button
            {
                Content = "Select Contour",
                Width = 200,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            okButton.Click += (sender, e) =>
            {
                if (contourComboBox.SelectedItem == null)
                {
                    MessageBox.Show("Please select a contour.");
                    return;
                }

                selectedId = contourComboBox.SelectedItem.ToString();
                window.DialogResult = true;
                window.Close();
            };

            spMain.Children.Add(titleBlock);
            spMain.Children.Add(infoBlock);
            spMain.Children.Add(contourComboBox);
            spMain.Children.Add(okButton);

            window.Title = "Select Contour for Junction Creation";
            window.Content = spMain;
            window.FontFamily = new System.Windows.Media.FontFamily("Calibri");
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            window.ShowDialog();

            return selectedId;
        }
        #endregion

        //

        /// <summary>
        /// Validates that the current context is a Patient
        /// <para></para>Will alert the user and end the script
        /// </summary>
        /// <param name="context"></param>
        private bool ValidatePatient(ScriptContext context)
        {
            if (context.Patient == null)
            {
                MessageBox.Show("Please open a patient");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates that the current context is a Course
        /// <para></para>Will alert the user and end the script
        /// </summary>
        /// <param name="context"></param>
        private bool ValidateCourse(ScriptContext context)
        {
            if (context.Course == null)
            {
                MessageBox.Show("Please open a course");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates that the current context is a Course
        /// <para></para>Will alert the user and end the script
        /// </summary>
        /// <param name="context"></param>
        private bool ValidateStructureSet(ScriptContext context)
        {
            if (context.StructureSet == null)
            {
                MessageBox.Show("Please open a structure set");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates that the current context is a plan
        /// <para></para>Will alert the user and end the script
        /// </summary>
        /// <param name="context"></param>
        private bool ValidatePlan(ScriptContext context)
        {
            if (context.StructureSet == null)
            {
                MessageBox.Show("Please open a plan");
                return false;
            }
            return true;
        }
    }
}


