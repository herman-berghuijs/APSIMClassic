using System;
using System.Collections.Generic;
using System.Text;
using ApsimFile;
using System.IO;
using System.Xml;
using System.Collections;
using CSGeneral;
using System.Threading;
using System.Globalization;

public class ApsimToSim
{
    /// <summary>
    /// Writes a sim file for the specified component. Returns the name of the 
    /// .sim file that was written. Will throw on error.
    /// </summary>
    /// 
    public static string WriteSimFile(Component Child, bool dontWriteSimFiles)
    {
        return WriteSimFile(Child, Directory.GetCurrentDirectory(), Configuration.getArchitecture(), dontWriteSimFiles);
    }

    public static string WriteSimFile(Component Child, Configuration.architecture arch, bool dontWriteSimFiles)
    {
        return WriteSimFile(Child, Directory.GetCurrentDirectory(), arch, dontWriteSimFiles);
    }

    public static string GetSimText(Component Child, Configuration.architecture arch)
    {
        return GetSimDoc(Child, arch).InnerXml;
    }

    /// <summary>
    /// Writes a sim file for the specified component. Will throw on error.
    /// </summary>
    /// 
    public static XmlDocument GetSimDoc(Component Child, Configuration.architecture arch)
    {
        // See if there is an overriding plugins component within scope of the Child passed in.
        // If so then tell PlugIns to load the plugins.
        Component PluginsOverride = ComponentUtility.FindComponentWithinScopeOf(Child, "PlugIns");
        if (PluginsOverride != null)
            PlugIns.LoadAllFromComponent(PluginsOverride);

        XmlDocument SimXML = new XmlDocument();
        string simText = WriteSimScript(Child, arch);
        SimXML.LoadXml(simText);
        SortSimContents(SimXML.DocumentElement);

        // Reinstate the original plugins if we overrode them at the start of this method.
        if (PluginsOverride != null)
            PlugIns.LoadAll();

        SimXML.InsertBefore(SimXML.CreateXmlDeclaration("1.0", "UTF-8", null), SimXML.DocumentElement);
        return SimXML;
    }

    private static string WriteSimFile(Component Child, string FolderName, Configuration.architecture arch, bool dontWriteSimFiles)
    {
        string SimFileName = FolderName + Path.DirectorySeparatorChar + Child.Name + ".sim";
        if (!dontWriteSimFiles)
        {
            StreamWriter fp = new StreamWriter(SimFileName);
            GetSimDoc(Child, arch).Save(fp);
            fp.Close();
        }
        return Path.GetFullPath(SimFileName);
    }

    private static string WriteSimScript(Component Child, Configuration.architecture arch)
    {
        // Write and return the .sim file contents for the specified 
        // Child component.
        if (Child.Enabled)
        {
            XmlNode ApsimToSim = Types.Instance.ApsimToSim(Child.Type);
            if (ApsimToSim != null)
            {
                ApsimToSim = ApsimToSim.CloneNode(deep: true);
                if (ApsimToSim.FirstChild.Name == "component" && XmlHelper.Attribute(ApsimToSim.FirstChild, "class") == "")
                {
                    string dllName = Types.Instance.MetaData(Child.Type, "dll");
                    // Make sure we're using the correct path separators for our current platform
                    // before making use of GetFileNameWithoutExtension to extract the name
                    dllName = dllName.Replace('/', Path.DirectorySeparatorChar);
                    dllName = dllName.Replace('\\', Path.DirectorySeparatorChar);
                    dllName = Path.GetFileNameWithoutExtension(dllName);
                    string className = Types.Instance.ProxyClassName(Child.Type, dllName);
                    if (className.ToLower() != dllName.ToLower())
                    {
                        if (className == "")
                            className = dllName;
                        else if (dllName != "")
                            className = dllName + "." + className;
                    }
                    if (className != "" && className != null)
                        XmlHelper.SetAttribute(ApsimToSim.FirstChild, "class", className);
                }
                else if (ApsimToSim.FirstChild.Name == "system" && Child.Type == "area" && XmlHelper.Attribute(ApsimToSim.FirstChild, "class") == "")
                    XmlHelper.SetAttribute(ApsimToSim.FirstChild, "class", "ProtocolManager");
                string ApsimToSimContents = ApsimToSim.InnerXml;

                // Replace any occurrences of our macros with appropriate text.
                // e.g. [Soil.] is replaced by the appropriate soil value.
                //      [Model] is replaced by the contents of the [Model] node i.e. the ini contents.
                //      [Dll] is replaced by the name of the model dll.
                //      [Children] is replaced by the sim script for all children of this component.
                //      [InstanceName] is replaced by the instance name.
                ApsimToSimContents = ApsimToSimContents.Replace("[InstanceName]", Child.Name);
                ApsimToSimContents = ReplaceSoilMacros(ApsimToSimContents, Child);
                ApsimToSimContents = ReplaceModelMacro(ApsimToSimContents, Child);
                ApsimToSimContents = ReplaceDllMacro(ApsimToSimContents, Child);
                ApsimToSimContents = ReplaceDllExtMacro(ApsimToSimContents, Child, arch);
                ApsimToSimContents = ReplaceChildrenMacro(ApsimToSimContents, Child, arch);

                // Any other macros in the <ApsimToSim> will be removed by using the 
                // APSIM macro language.
                XmlDocument ChildValues = new XmlDocument();
                ChildValues.LoadXml(Child.Contents);

                // Add in any child components that don't have anything in their <ApsimToSim>
                foreach (Component SubChild in Child.ChildNodes)
                {
                    if (SubChild.Enabled)
                    {
                        // This next if stmt is for SoilTemperature 1 and 2 which have a <ApsimToSim> for when they
                        // are not under a <soil>
                        if (Child.Type == "Soil")
                        {
                            XmlNode soilNode = SubChild.ContentsAsXML;
                            ChildValues.DocumentElement.AppendChild(ChildValues.ImportNode(soilNode, true));
                        }
                        else
                            RecursivelyAddChildContent(SubChild, ChildValues.DocumentElement);
                    }
                }
                Macro Macro = new Macro();
                return Macro.Go(ChildValues.DocumentElement, XmlHelper.FormattedXML(ApsimToSimContents));
            }
        }
        return "";
    }
    private static void RecursivelyAddChildContent(Component C, XmlNode ContentNode)
    {
        // Recursively add in any child components that don't have anything in their <ApsimToSim>
        XmlNode ApsimToSim = Types.Instance.ApsimToSim(C.Type);
        if (ApsimToSim == null)
        {
            XmlNode NewNode = ContentNode.AppendChild(ContentNode.OwnerDocument.ImportNode(C.ContentsAsXML, true));
            foreach (Component Child in C.ChildNodes)
                RecursivelyAddChildContent(Child, NewNode);
        }
    }
    private static string ReplaceDllMacro(string ApsimToSimContents, Component ApsimComponent)
    {
        // Replace all occurrences of [Dll] with the name of the model dll.
        string Dll = Types.Instance.MetaData(ApsimComponent.Type, "dll");
        Dll = Dll.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        Dll = Configuration.AddMacros(Dll);
        return ApsimToSimContents.Replace("[dll]", Dll);
    }

    private static string ReplaceDllExtMacro(string ApsimToSimContents, Component ApsimComponent, Configuration.architecture arch)
    {
        // Replace all occurrences of %dllext%
        if (arch == Configuration.architecture.unix) // ApsimFile.Configuration.amRunningOnUnix()
            return (ApsimToSimContents.Replace("%dllext%", "so"));

        return (ApsimToSimContents.Replace("%dllext%", "dll"));
    }
    private static string ReplaceModelMacro(string ApsimToSimContents, Component ApsimComponent)
    {
        // Replace all occurrences of [Model] with the contents of the model configuration.
        while (ApsimToSimContents.Contains("[Model"))
        {
            // See if there is something after [Model e.g. [Model SoilWat]. SoilWat is the ModelType
            int PosModel = ApsimToSimContents.IndexOf("[Model");
            int PosEndModel = ApsimToSimContents.IndexOf(']', PosModel);
            int PosStartModelType = PosModel + "[Model".Length;
            string ModelType = ApsimToSimContents.Substring(PosStartModelType, PosEndModel - PosStartModelType).Trim();

            // If the user has an ini child under
            string ModelContents = GetModelContents(ApsimComponent, ModelType);

            ApsimToSimContents = ApsimToSimContents.Remove(PosModel, PosEndModel - PosModel + 1);
            ApsimToSimContents = ApsimToSimContents.Insert(PosModel, ModelContents);
            if (ModelType != "")
            {
                PosEndModel = PosModel + ModelContents.Length;
                int compPos = ApsimToSimContents.LastIndexOf("<component ", PosModel);
                int prevCompEnd = ApsimToSimContents.LastIndexOf("</component>", PosModel);
                int compEnd = ApsimToSimContents.IndexOf("</component>", PosEndModel);
                // Was the [Model macro embedded inside a "component" element? If so, add a class attribute to that element
                if (prevCompEnd < compPos && compEnd > 0)
                {
                    XmlDocument bodyDoc = new XmlDocument();
                    bodyDoc.LoadXml(ApsimToSimContents.Substring(compPos, compEnd - compPos + 12));
                    if (bodyDoc.FirstChild.Name == "component" && XmlHelper.Attribute(bodyDoc.FirstChild, "class") == "")
                    {
                        string dllName = Path.GetFileNameWithoutExtension(Types.Instance.MetaData(ModelType, "dll"));
                        string className = Types.Instance.ProxyClassName(ModelType, dllName);
                        if (className.ToLower() != dllName.ToLower())
                        {
                            if (className == "")
                                className = dllName;
                            else if (dllName != "")
                                className = dllName + "." + className;
                        }
                        if (className != "")
                            XmlHelper.SetAttribute(bodyDoc.FirstChild, "class", className);
                    }
                    string newText = bodyDoc.OuterXml;
                    ApsimToSimContents = ApsimToSimContents.Remove(compPos, compEnd - compPos + 12);
                    ApsimToSimContents = ApsimToSimContents.Insert(compPos, newText);
                }
            }
        }
        return ApsimToSimContents;
    }

    private static string GetModelContents(Component ApsimComponent, string ModelType)
    {
        string ModelContents = "";
        foreach (Component Child in ApsimComponent.ChildNodes)
        {
            if (Child.Type == "ini")
            {
                // Get the name of the model file.
                XmlDocument IniComponent = new XmlDocument();
                IniComponent.LoadXml(Child.Contents);
                string ModelFileName = Configuration.RemoveMacros(XmlHelper.Value(IniComponent.DocumentElement, "filename"));
                ModelFileName = ModelFileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                if (Path.GetExtension(ModelFileName) == ".xml")
                {
                    // Find the <Model> node in the model file.
                    XmlDocument ModelFile = new XmlDocument();
                    ModelFile.Load(ModelFileName);
                    if (ModelType == "")
                        ModelType = ApsimComponent.Type;
                    ModelContents += FindModelContents(ModelFile.DocumentElement, ModelType);
                }
                else
                    ModelContents += "<include>" + ModelFileName + "</include>";
            }
        }

        // If we didn't find an ini component then look in the standard XML.
        if (ModelContents == "")
        {
            if (ModelType == "")
                ModelContents = Types.Instance.ModelContents(ApsimComponent.Type);
            else
                ModelContents = Types.Instance.ModelContents(ApsimComponent.Type, ModelType);
        }

        return ModelContents;
    }
    private static string FindModelContents(XmlNode Node, string TypeName)
    {
        // Given the XmlNode passed in, try and find the <Model> node.
        // The node passed in could be a <type> node in which case the <Model>
        // node will be immediately under it.
        // Alternatively, the node passed in could be a <plugin> node, so the 
        // <Model> node will be under the <type name="xxx"> node.
        XmlNode ModelNode = XmlHelper.Find(Node, "Model");
        if (ModelNode == null)
            ModelNode = XmlHelper.Find(Node, TypeName + "/Model");
        if (ModelNode == null)
            ModelNode = XmlHelper.Find(Node, TypeName);
        if (ModelNode == null)
            return "";
        else
            return ModelNode.InnerXml;
    }
    private static string ReplaceSoilMacros(string ApsimToSimContents, Component ApsimComponent)
    {
        // Replace the [soil.] macros with values.
        if (ApsimToSimContents.Contains("[soil."))
        {
            // Firstly we need to locate the nearest soil.
            Component SoilComponent = null;
            if (ApsimComponent.Type.ToLower() == "soil")
                SoilComponent = ApsimComponent;
            else
            {
                foreach (Component Sibling in ApsimComponent.Parent.ChildNodes)
                {
                    if (Sibling.Type.ToLower() == "soil")
                        SoilComponent = Sibling;
                }
            }

            if (SoilComponent != null)
            {
                // Create a soil XML node.
                XmlDocument Doc = new XmlDocument();
                Doc.LoadXml(SoilComponent.FullXMLNoShortCuts());
                XmlNode SoilNode = Doc.DocumentElement;

                

                // Now do all soil macro replacements.
                ApsimToSimContents = ReplaceSoilMacros(SoilNode, ApsimToSimContents);
            }
        }
        return ApsimToSimContents;
    }

    private static string ReplaceSoilMacros(XmlNode SoilNode, string ApsimToSimContents)
    {
        // Get rid of nodes under <soil> that are disabled.
        RemoveDisabledNodes(SoilNode);

        APSIM.Shared.Soils.Soil mySoil = APSIM.Shared.Soils.SoilUtilities.FromXML(SoilNode.OuterXml);
        mySoil = APSIM.Shared.Soils.APSIMReadySoil.Create(mySoil);
        if (mySoil.Name == null)
            mySoil.Name = "Soil";

        // Loop through all soil macros.
        int PosMacro = 0;
        PosMacro = ApsimToSimContents.IndexOf("[soil.", PosMacro);
        while (PosMacro != -1)
        {
            int PosEndMacro = ApsimToSimContents.IndexOf(']', PosMacro);
            if (PosEndMacro == -1)
                throw new Exception("Invalid soil macro found: " + ApsimToSimContents.Substring(PosMacro));

            // Get macro name e.g. soil.thickness
            string MacroName = ApsimToSimContents.Substring(PosMacro + 1, PosEndMacro - PosMacro - 1);

            //if (MacroName.Contains("soil."))
            {
                // remove the soil. prefix from the MacroName.
                MacroName = MacroName.Substring(MacroName.IndexOf('.') + 1);

                // Is it a crop macro i.e. wheat ll
                object Obj = null;
                if (MacroName.Contains(" "))
                {
                    string CropName = MacroName.Substring(0, MacroName.IndexOf(' '));
                    string VariableName = MacroName.Substring(MacroName.IndexOf(' ') + 1);
                    APSIM.Shared.Soils.SoilCrop crop = mySoil.Water.Crops.Find(c => c.Name.Equals(CropName, StringComparison.InvariantCultureIgnoreCase));
                    if (crop == null)
                        throw new Exception("Cannot find soil water information for crop '" + CropName + "'. " +
                            "You likely need to \"Manage Crops\" in the Water node of your soil, and set the water properties of this crop" );
                    if (VariableName.Equals("ll", StringComparison.CurrentCultureIgnoreCase))
                        Obj = crop.LL;
                    else if (VariableName.Equals("kl", StringComparison.CurrentCultureIgnoreCase))
                        Obj = crop.KL;
                    else if (VariableName.Equals("xf", StringComparison.CurrentCultureIgnoreCase))
                        Obj = crop.XF;
                }
                else
                    Obj = Utility.GetValueOfFieldOrProperty(MacroName, mySoil);

                if (Obj != null)
                {
                    string MacroValue = "";
                    if (Obj is IList)
                    {
                        foreach (object I in Obj as IList)
                        {
                            if (I is double)
                                MacroValue += ((double)I).ToString("f3", CultureInfo.CreateSpecificCulture("en-AU")) + " ";
                            else
                                MacroValue += I.ToString() + " ";
                        }
                    }
                    else if (Obj is double)
                    {
                        if (double.IsNaN(Convert.ToDouble(Obj)))
                            MacroValue = "";
                        else
                            MacroValue = ((Double)Obj).ToString("f3", CultureInfo.CreateSpecificCulture("en-AU"));
                    }
                    else
                        MacroValue = Obj.ToString();

                    ApsimToSimContents = ApsimToSimContents.Remove(PosMacro, PosEndMacro - PosMacro + 1);
                    ApsimToSimContents = ApsimToSimContents.Insert(PosMacro, MacroValue);
                }
                PosMacro = ApsimToSimContents.IndexOf("[soil.", PosMacro + 1);
            }
        }

        return ApsimToSimContents;
    }

    private static void RemoveDisabledNodes(XmlNode SoilNode)
    {
        List<XmlNode> children = XmlHelper.ChildNodes(SoilNode, "");
        foreach (XmlNode child in children)
        {
            if (XmlHelper.Attribute(child, "enabled").Equals("no", StringComparison.CurrentCultureIgnoreCase))
                SoilNode.RemoveChild(child);
        }
    }


    private static string ReplaceChildrenMacro(string ApsimToSimContents, Component ApsimComponent, Configuration.architecture arch)
    {
        // Replace the [Children] macro with child sim script.

        int PosStartMacro = ApsimToSimContents.IndexOf("[Children");
        while (PosStartMacro != -1)
        {
            int PosEndMacro = ApsimToSimContents.IndexOf(']', PosStartMacro);
            if (PosEndMacro != -1)
            {
                string ChildType = "";
                string[] MacroWords = ApsimToSimContents.Substring(PosStartMacro + 1, PosEndMacro - PosStartMacro - 1)
                                      .Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                if (MacroWords.Length == 2)
                    ChildType = MacroWords[1];

                StringBuilder ChildSimContents = new StringBuilder();
                foreach (Component Child in ApsimComponent.ChildNodes)
                {
                    if (ChildType == "" || Child.Type.ToLower() == ChildType.ToLower())
                        ChildSimContents.Append(WriteSimScript(Child, arch));
                }
                ApsimToSimContents = ApsimToSimContents.Remove(PosStartMacro, PosEndMacro - PosStartMacro + 1);
                ApsimToSimContents = ApsimToSimContents.Insert(PosStartMacro, ChildSimContents.ToString());
            }
            PosStartMacro = ApsimToSimContents.IndexOf("[Children", PosStartMacro + 1);
        }
        if (ApsimToSimContents.Contains("[HasChildren"))
        {

            StringBuilder ChildSimContents = new StringBuilder();
            foreach (Component Child in ApsimComponent.ChildNodes)
                ChildSimContents.Append(WriteSimScript(Child, arch));
            if (ChildSimContents.Length == 0)
                ApsimToSimContents = ApsimToSimContents.Replace("[HasChildren]", "");
            else
                ApsimToSimContents = ApsimToSimContents.Replace("[HasChildren]", "Yes");
            ApsimToSimContents = ApsimToSimContents.Replace("[Children]", ChildSimContents.ToString());
        }
        return ApsimToSimContents;
    }

    #region Sim Sorting code
    private static void SortSimContents(XmlNode Node)
    {
        // go through all paddocks and sort all component nodes.

        XmlHelper.Sort(Node, new ComponentSorter());
        foreach (XmlNode System in XmlHelper.ChildNodes(Node, "system"))
            SortSimContents(System); // recursion
    }
    private class ComponentSorter : IComparer
    {
        //private CaseInsensitiveComparer StringComparer = new CaseInsensitiveComparer();
        private List<string> Components = new List<string>();
        public ComponentSorter()
        {
            Components = Configuration.Instance.ComponentOrder();
        }
        int IComparer.Compare(object x, object y)
        {
            XmlNode Data1 = (XmlNode)x;
            XmlNode Data2 = (XmlNode)y;
            string ModuleName1 = Path.GetFileNameWithoutExtension(XmlHelper.Attribute(Data1, "executable")).ToLower();
            string ModuleName2 = Path.GetFileNameWithoutExtension(XmlHelper.Attribute(Data2, "executable")).ToLower();
            bool ambiguous = false;

            if (x == y)
                return 0;
            if (Data1.Name == "executable")
                return -1;
            if (Data2.Name == "executable")
                return 1;
            if (ModuleName1 == ModuleName2)
            {
                int ChildIndex1 = Array.IndexOf(XmlHelper.ChildNames(Data1.ParentNode, ""), XmlHelper.Name(Data1));
                int ChildIndex2 = Array.IndexOf(XmlHelper.ChildNames(Data2.ParentNode, ""), XmlHelper.Name(Data2));
                if (ChildIndex1 < ChildIndex2)
                    return -1;
                else if (ChildIndex2 > ChildIndex1)
                    return 1;
                else
                    ambiguous = true;
            }
            if (XmlHelper.Type(Data1) == "title")
                return -1;
            if (!ambiguous)
            {
                for (int i = 0; i != Components.Count; i++)
                {
                    if (StringManip.StringsAreEqual(Components[i], ModuleName1))
                        return -1;
                    if (StringManip.StringsAreEqual(Components[i], ModuleName2))
                        return 1;
                }
            }
            // Neither are in list so keep original order intact i.e. Node1 comes before Node2!!
            // Find the relative positions of data1 and data2 in the parent list.
            int Data1Pos = 0;
            int Data2Pos = 0;
            for (int i = 0; i != Data1.ParentNode.ChildNodes.Count; i++)
            {
                if (Data1.ParentNode.ChildNodes[i] == Data1)
                    Data1Pos = i;
                if (Data1.ParentNode.ChildNodes[i] == Data2)
                    Data2Pos = i;
            }
            if (Data1Pos < Data2Pos)
                return -1;
            else
                return 1;
        }
    }
    #endregion

}
