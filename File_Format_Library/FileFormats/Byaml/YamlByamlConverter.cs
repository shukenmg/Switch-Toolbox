﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using AampLibraryCSharp.IO;
using SharpYaml.Serialization;
using System.Globalization;
using Syroot.BinaryData;
using System.Collections.Generic;

namespace FirstPlugin
{
    public class YamlByamlConverter
    {
        //Generate a list of all complex dynamic values that are used for reference nodes
        //The tag will be an ID to set the dynamic value
        private static Dictionary<dynamic, YamlNode> NodePaths = new Dictionary<dynamic, YamlNode>();

        //Used for saving by mapping tags to dynamic values
        private static Dictionary<string, dynamic> ReferenceNodes = new Dictionary<string, dynamic>();

        //id to keep track of reference nodes
        static int refNodeId = 0;

        public static string ToYaml(ByamlExt.Byaml.BymlFileData data)
        {
            var settings = new SerializerSettings()
            {
                EmitTags = false,
                EmitAlias = false,
                DefaultStyle = SharpYaml.YamlStyle.Flow,
                SortKeyForMapping = false,
                EmitShortTypeName = true,
                EmitCapacityForList = false,
                LimitPrimitiveFlowSequence = 4,
            };

            settings.RegisterTagMapping("!u", typeof(uint));
            settings.RegisterTagMapping("!l", typeof(int));
            settings.RegisterTagMapping("!d", typeof(double));
            settings.RegisterTagMapping("!ul", typeof(ulong));
            settings.RegisterTagMapping("!ll", typeof(long));

            var serializer = new Serializer(settings);
            return serializer.Serialize(data);

            NodePaths.Clear();
            refNodeId = 0;

            YamlNode root = SaveNode("root", data.RootNode);
            YamlMappingNode mapping = new YamlMappingNode();
            mapping.Add("Version", data.Version.ToString());
            mapping.Add("IsBigEndian", (data.byteOrder == ByteOrder.BigEndian).ToString());
            mapping.Add("SupportPaths", data.SupportPaths.ToString());
            mapping.Add("HasReferenceNodes", (refNodeId != 0).ToString());
            mapping.Add("root", root);

            NodePaths.Clear();
            refNodeId = 0;
            var doc = new YamlDocument(mapping);

            YamlStream stream = new YamlStream(doc);
            var buffer = new StringBuilder();
            using (var writer = new StringWriter(buffer)) {
                stream.Save(writer, true);
                return writer.ToString();
            }
        }

        public static ByamlExt.Byaml.BymlFileData FromYaml(string text)
        {
            NodePaths.Clear();
            ReferenceNodes.Clear();

            var data = new ByamlExt.Byaml.BymlFileData();
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            var mapping = (YamlMappingNode)yaml.Documents[0].RootNode;

            foreach (var child in mapping.Children)
            {
                var key = ((YamlScalarNode)child.Key).Value;
                var value = child.Value.ToString();

                if (key == "Version")
                    data.Version = ushort.Parse(value);
                if (key == "IsBigEndian")
                    data.byteOrder = bool.Parse(value) ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
                if (key == "SupportPaths")
                    data.SupportPaths = bool.Parse(value);

                if (child.Value is YamlMappingNode)
                    data.RootNode = ParseNode(child.Value);
                if (child.Value is YamlSequenceNode)
                    data.RootNode = ParseNode(child.Value);
            }

            ReferenceNodes.Clear();
            NodePaths.Clear();

            return data;
        }

        static dynamic ParseNode(YamlNode node)
        {
            if (node is YamlMappingNode)
            {
                var values = new Dictionary<string, dynamic>();
                if (IsValidReference(node))
                    ReferenceNodes.Add(node.Tag, values);

                foreach (var child in ((YamlMappingNode)node).Children)
                    values.Add(((YamlScalarNode)child.Key).Value, ParseNode(child.Value));
                return values;
            }
            else if (node is YamlSequenceNode) {
                var values = new List<dynamic>();
                if (IsValidReference(node))
                    ReferenceNodes.Add(node.Tag, values);

                foreach (var child in ((YamlSequenceNode)node).Children)
                    values.Add(ParseNode(child));
                return values;
            } //Reference node
            else if (node is YamlScalarNode && ((YamlScalarNode)node).Value.Contains("!refTag=")) {
                string tag = ((YamlScalarNode)node).Value.Replace("!refTag=", string.Empty);
                Console.WriteLine($"refNode {tag} {ReferenceNodes.ContainsKey(tag)}");
                if (ReferenceNodes.ContainsKey(tag))
                    return ReferenceNodes[tag];
                else {
                    Console.WriteLine("Failed to find reference node! " + tag);
                    return null;
                }
            }
            else {
                return ConvertValue(((YamlScalarNode)node).Value, ((YamlScalarNode)node).Tag);
            }
        }

        static bool IsValidReference(YamlNode node) {
            return node.Tag != null && node.Tag.Contains("!ref") && !ReferenceNodes.ContainsKey(node.Tag);
        }

        static dynamic ConvertValue(string value, string tag)
        {
            if (tag == null)
                tag = "";

            if (value == "null")
                return null;
            else if (value == "true")
                return true;
            else if (value == "false")
                return false;
            else if (tag == "!u")
                return UInt32.Parse(value, CultureInfo.InvariantCulture);
            else if (tag == "!l")
                return Int32.Parse(value, CultureInfo.InvariantCulture);
            else if (tag == "!d")
                return Double.Parse(value, CultureInfo.InvariantCulture);
            else if (tag == "!ul")
                return UInt64.Parse(value, CultureInfo.InvariantCulture);
            else if (tag == "!ll")
                return Int64.Parse(value, CultureInfo.InvariantCulture);
            else
            {
                float floatValue = 0;
                bool isFloat = float.TryParse(value, out floatValue);
                if (isFloat)
                    return floatValue;
                else
                    return value;
            }
        }

        static YamlNode SaveNode(string name, dynamic node)
        {
            if (node == null) {
                return new YamlScalarNode("null");
            }
            else if (NodePaths.ContainsKey(node))
            {
                if (NodePaths[node].Tag == null)
                    NodePaths[node].Tag = $"!ref{refNodeId++}";
                return new YamlScalarNode($"!refTag={NodePaths[node].Tag}");
            }
            else if ((node is IList<dynamic>))
            {
                var yamlNode = new YamlSequenceNode();
                NodePaths.Add(node, yamlNode);

                if (!HasEnumerables((IList<dynamic>)node) &&
                    ((IList<dynamic>)node).Count < 6)
                    yamlNode.Style = SharpYaml.YamlStyle.Flow;

                foreach (var item in (IList<dynamic>)node)
                    yamlNode.Add(SaveNode(null, item));

                return yamlNode;
            }
            else if (node is IDictionary<string, dynamic>)
            {
                var yamlNode = new YamlMappingNode();
                NodePaths.Add(node, yamlNode);

                if (!HasEnumerables((IDictionary<string, dynamic>)node) &&
                    ((IDictionary<string, dynamic>)node).Count < 6)
                    yamlNode.Style = SharpYaml.YamlStyle.Flow;

                foreach (var item in (IDictionary<string, dynamic>)node)
                    yamlNode.Add(item.Key, SaveNode(item.Key, item.Value));
                return yamlNode;
            }
            else
            {
                string tag = null;
                if (node is int) tag = "!l";
                else if (node is uint) tag = "!u";
                else if (node is Int64) tag = "!ul";
                else if (node is double) tag = "!d";

                var yamlNode = new YamlScalarNode(ConvertValue(node));
                if (tag != null) yamlNode.Tag = tag;
                return yamlNode;
            }
        }

        private static bool HasEnumerables(IDictionary<string, dynamic> node)
        {
            foreach (var item in node.Values)
            {
                if (item == null)
                    continue;
                if (item is IList<dynamic>)
                    return true;
                else if (item is IDictionary<string, dynamic>)
                    return true;
            }
            return false;
        }

        private static bool HasEnumerables(IList<dynamic> node)
        {
            foreach (var item in node)
            {
                if (node == null)
                    continue;
                if (node is IList<dynamic>)
                    return true;
                else if (node is IDictionary<string, dynamic>)
                    return true;
            }
            return false;
        }

        private static string ConvertValue(dynamic node)
        {
            if (node is bool) return ((bool)node) ? "true" : "false";
            else if (node is float) return string.Format("{0:0.000.00}", node);
            else return node.ToString();
        }
    }
}
