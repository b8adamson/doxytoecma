// DoxyToEcma
// 
// Transforms Doxy XML documents to ECMA XML documents, by matching typenames
// and Export attributes in MonoTouch bindings.
//
// Authors:
//   Miguel de Icaza
//
// TODO:
//    If briefdescription is present, use that for <summary> instead of
//    the current setup that takes the first paragraph from the full detailed
//    description as the summary.
//
//    Import enumerations
//
using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.IO;
using System.Collections.Generic;

namespace DoxyToEcma
{
	class Importer
	{
		// key is the plain type (no namespace, points to ECMA XML file)
		Dictionary <string,XDocument> ecma_docs = new Dictionary<string, XDocument> ();
		Dictionary <string,XDocument> doxy_docs = new Dictionary<string, XDocument> ();
		Dictionary <string, string> ecma_full = new Dictionary<string, string> ();
		Dictionary <string, string> ecma_path = new Dictionary<string, string> ();

		public static void Main (string[] args)
		{
			new Importer ().Run (args);
		}

		void Usage ()
		{
			Console.WriteLine ("usage is: DoxyToEcma ecma_dir doxy_xml_dir");

		}

		public void Run (string [] args)
		{
			string ecma, doxy;

			if (args.Length == 2) {
				ecma = args [0];
				doxy = args [1];
			} else {
				Usage ();
				return;
			}
			Console.WriteLine ("Loading Doxy from {0}", doxy);
			LoadDoxyDocs (doxy);
			LoadEcmaDocs (ecma);
			MergeDocs ();
			SaveEcmaDocs ();
		}

		void SaveEcmaDocs ()
		{
			foreach (var kv in ecma_docs) {
				var settings = new XmlWriterSettings () {
					Indent = true,
					NewLineChars = "\n",
				};
				using (var output = XmlWriter.Create (ecma_path [kv.Key], settings))
					kv.Value.Save (output);
			}
		}

		void LoadEcmaDocs (string ecmaDir)
		{

			var doc = XDocument.Load (Path.Combine (ecmaDir, "index.xml"));
			foreach (var node in doc.XPathSelectElements ("/Overview/Types/*/Type")) {
				var kind = node.Attribute ("Kind").Value;

				if (kind != "Class" && kind != "Structure")
					continue;

				var ns = node.Parent.FirstAttribute.Value;
				var tn = node.Attribute ("Name").Value;

				var path = Path.Combine (ecmaDir, ns, tn + ".xml");
				ecma_path [tn] = path;
				ecma_docs [tn] = XDocument.Load (path);
				ecma_full [tn] = ns + "." + tn;
			}
		}

		bool debug;

		void MergeDocs ()
		{
			foreach (string type in ecma_docs.Keys) {
				XDocument doxy, ecma = ecma_docs [type];
				if (!doxy_docs.TryGetValue (type, out doxy)){
					if (debug)
						Console.WriteLine ("Warning: no doxy docs for {0}", type);
					continue;
				}
				debug = (type == "CCNode");
					
				ImportDoxyDoc (type, ecma, doxy);
			}
		}

		//
		// Transforms the detaileddescription section
		// @element contains the XML fragment for the section
		// 
		// Returns the Tuple for a "parameterlist" and "parameterDescription" nodes, or null if not available.
		//
		Tuple<XElement,XElement> TransformDoxy (XElement element)
		{
			XElement pname;
			Tuple<XElement,XElement> parameterList = null;

			if (debug) Console.WriteLine ("BEFORE: " + element);
			var removeList = new List<XElement> ();
			foreach (var e in element.Descendants ()){
				var s = e.Name.ToString ();
				switch (s){
				case "ref":
					var kind = e.Attribute ("kindref").Value;
					switch (kind){
					case "compound":
						if (ecma_docs.ContainsKey (e.Value)){
							var full = ecma_full [e.Value];
							
							e.Name = "see";
							e.RemoveAll ();
							
							e.SetAttributeValue ("cref", "T:" + full);
						}
						break;
					default:
						Console.WriteLine ("Do not know how to handle kindref: " + e.Attribute ("kindref").Value);
						break;
					}
					break;
				case "orderedlist":
					e.Name = "list";
					e.SetAttributeValue ("type", "number");
					break;
				case "itemizedlist":
					e.Name = "list";
					e.SetAttributeValue ("type", "bullet");
					break;
				case "listitem":
					e.ReplaceWith (new XElement ("item", new XElement ("description", e.Value)));
					break;
				case "para":
					break;
				case "parameterlist":
					parameterList = new Tuple<XElement,XElement> (e.XPathSelectElement ("parameteritem/parameternamelist"), e.XPathSelectElement ("parameteritem/parameterdescription"));
					removeList.Add (e); 
					break;
				default:
					if (debug) Console.WriteLine ("Unhandled: " + s);
					break;
				}
			}
			if (removeList.Count > 0) {
				foreach (var p in removeList)
					p.Remove ();
			}

			if (debug)
				Console.WriteLine ("\n\nAFTER: {0}\n\n\n\n", element);
			return parameterList;
		}

		Tuple<XElement,XElement> Plug (XContainer target, string summaryPath, string remarksPath, XElement doxyDocs)
		{
			var ret = TransformDoxy (doxyDocs);
			var elements = doxyDocs.Elements ();
			var sum = target.XPathSelectElement (summaryPath);
			var rem = target.XPathSelectElement (remarksPath);
			var first = elements.FirstOrDefault ();
			if (first != null){
				sum.SetValue (first);
				rem.SetValue (elements);
			}
			return ret;
		}

		void ImportDoxyDoc (string typeName, XDocument ecmaDoc, XDocument doxyDoc)
		{

			// Bring class details.
			var details = doxyDoc.XPathSelectElement ("/doxygen/compounddef/detaileddescription");
			if (details != null)
				Plug (ecmaDoc, "/Type/Docs/summary", "/Type/Docs/remarks", details);

			// Bring property docs
			Tuple<XElement,XElement> parameterList;

			foreach (var property in doxyDoc.XPathSelectElements ("/doxygen/compounddef/sectiondef/memberdef[@kind='property']")){

				var name = property.XPathSelectElement ("name").Value;
				var detailed = property.XPathSelectElement ("detaileddescription");

				var ecmaNode = ecmaDoc.XPathSelectElement ("/Type/Members/Member[Attributes/Attribute/AttributeName='get: MonoTouch.Foundation.Export(\"" + name +"\")']");
				if (debug)
					Console.WriteLine ("Found Node: {0} {1}", name, ecmaNode != null);
				if (ecmaNode == null){
					Console.WriteLine ("Warning: did not find this selector {0} on the {1} type", name, typeName);
					continue;
				}
				parameterList = Plug (ecmaNode, "Docs/summary", "Docs/remarks", detailed);
			}

			foreach (var method in doxyDoc.XPathSelectElements ("/doxygen/compounddef/sectiondef/memberdef[@kind='function']")){
				var name = method.XPathSelectElement ("name").Value;
				var detailed = method.XPathSelectElement ("detaileddescription");

				var ecmaNode = ecmaDoc.XPathSelectElement ("/Type/Members/Member[Attributes/Attribute/AttributeName='MonoTouch.Foundation.Export(\"" + name +"\")']");
				if (debug)
					Console.WriteLine ("Found Node: {0} {1}", name, ecmaNode != null);
				if (ecmaNode == null){
					Console.WriteLine ("Warning: did not find this selector {0} on the {1} type", name, typeName);
					continue;
				}
				parameterList = Plug (ecmaNode, "Docs/summary", "Docs/remarks", detailed);
				if (parameterList != null){
					var names = parameterList.Item1.XPathSelectElements ("parametername");
					var descs = parameterList.Item2.Descendants ();

					var pairs = names.Zip (descs, (first, second) => Tuple.Create (first.Value, second));
	
					foreach (var parameter in pairs){
						var exp = "Docs/param[@name='" + parameter.Item1 + "']";
						var pnode = ecmaNode.XPathSelectElement (exp);
						if (pnode != null){

							TransformDoxy (parameter.Item2);
							pnode.SetValue (parameter.Item2);
						}
					}
				}
			}

			if (debug)
				ecmaDoc.Save ("/tmp/foo.xml");
#if false
			// Bring API docs, for each API that has an exported selector
			var members = 
				from member in ecmaDoc.XPathSelectElements ("/Type/Members/Member[Attributes/Attribute/AttributeName]")
				let attrs = member.Descendants ("Attributes").Descendants ("Attribute").Descendants ("AttributeName")
					from attr in attrs 
					where attr.Value.IndexOf ("ExportAttribute") != -1
					select member;
#endif

		}

		void LoadDoxyDocs (string doxyDir)
		{
			foreach (var f in Directory.GetFiles (doxyDir, "*.xml")) {
				if (!f.StartsWith ("interface") && f.StartsWith ("protocol") && !f.StartsWith ("struct"))
					continue;

				var doc = XDocument.Load (Path.Combine (doxyDir, f));
				if (doc == null)
					continue;
				var name = doc.XPathSelectElement ("/doxygen/compounddef/compoundname");
				if (name == null)
					continue;

				doxy_docs [name.Value] = doc;
			}
		}
	}
}
