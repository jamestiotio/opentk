using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

namespace CHeaderToXML
{
    class GLXmlParser : Parser
    {
        string EnumPrefix { get { return Prefix.ToUpper() + "_"; } }
        string FuncPrefix { get { return Prefix; } }

        public GLXmlParser()
        {
        }

        public override IEnumerable<XElement> Parse(string[] lines)
        {
            var input = XDocument.Parse(String.Join(" ", lines));

            List<XElement> elements = new List<XElement>();
            elements.AddRange(ParseEnums(input));
            elements.AddRange(ParseFunctions(input));

            return elements;
        }

        IEnumerable<XElement> ParseEnums(XDocument input)
        {
            var features = input.Root.Elements("feature");
            var extensions = input.Root.Elements("extensions").Elements("extension");
            var enumerations = input.Root.Elements("enums").Elements("enum");
            var groups = input.Root.Elements("groups").Elements("group");
            var APIs = new Dictionary<string, XElement>();

            // Build a list of all available tokens.
            // Some tokens have a different value between GL and GLES,
            // so we need to keep separate lists for each API. Tokens
            // that are common go to the "default" list.
            var enums = new Dictionary<string, SortedDictionary<string, string>>();
            foreach (var e in enumerations)
            {
                var api = (e.Attribute("api") ?? new XAttribute("api", "default")).Value;
                if (!enums.ContainsKey(api))
                    enums.Add(api, new SortedDictionary<string, string>());

                enums[api].Add(
                    TrimName(e.Attribute("name").Value),
                    e.Attribute("value").Value);
            }

            // Now we go through each feature, extension and group
            // and construct the actual enumerations. Each API
            // gets its own list of enumerations. Common enums are
            // explicitly repeated. For example:
            // <extension name="GL_AMD_performance_monitor" supported="gl|gles2">
            // means that its enums must go to both the gl and gles2 APIs.
            foreach (var feature in features.Concat(extensions).Concat(groups))
            {
                var category = TrimName(feature.Attribute("name").Value);
                var extension = feature.Name == "extension" ? category.Substring(0, category.IndexOf("_")) : "Core";
                var version = feature.Attribute("number") != null ? feature.Attribute("number").Value : null;
                var apinames =
                    (feature.Attribute("api") != null ? feature.Attribute("api").Value :
                    feature.Attribute("supported") != null ? feature.Attribute("supported").Value :
                    "gl").Split('|');

                // An enum may belong to one or more APIs.
                // Add it to all relevant ones.
                foreach (var apiname in apinames)
                {
                    if (!APIs.ContainsKey(apiname))
                        APIs.Add(apiname, new XElement("api", new XAttribute("name", apiname)));
                    var api = APIs[apiname];

                    var enum_name = TrimName(feature.Attribute("name").Value);

                    var e = new XElement("enum", new XAttribute("name", enum_name));
                    foreach (var token in feature.Elements("enum").Concat(feature.Elements("require").Elements("enum")))
                    {
                        var token_name = TrimName(token.Attribute("name").Value);
                        var token_value =
                            enums.ContainsKey(apiname) && enums[apiname].ContainsKey(token_name) ? enums[apiname][token_name] :
                            enums["default"].ContainsKey(token_name) ? enums["default"][token_name] :
                            String.Empty;

                        if (!String.IsNullOrEmpty(token_value))
                        {
                            var @enum =
                                new XElement(
                                    "token",
                                    new XAttribute("name", token_name),
                                    new XAttribute("value", token_value));
                            e.Add(@enum);
                        }
                        else
                        {
                            Trace.WriteLine(String.Format("Token {0} is not defined.", token_name));
                        }
                    }

                    foreach (var token in feature.Elements("remove").Elements("enum"))
                    {
                        var token_name = TrimName(token.Attribute("name").Value);
                        var deprecated =
                            api.Elements("enum").Elements("token")
                            .FirstOrDefault(t => t.Attribute("name").Value == token_name);
                        if (deprecated != null)
                        {
                            deprecated.Add(new XAttribute("deprecated", version));
                        }
                    }

                    api.Add(e);
                }
            }

            return APIs.Values;
        }

        IEnumerable<XElement> ParseFunctions(XDocument input)
        {
            //  Go through the list of commands and build OpenTK functions out of those.
            // Every function has a number of attributes that define which API version and
            // category (see above) they belong to.
            // It also includes information about the return type and parameters. These
            // are then parsed by the binding generator in order to create the necessary
            // overloads for correct use.
            var features = input.Root.Elements("feature");
            var extensions = input.Root.Elements("extensions").Elements("extension");
            var APIs = new Dictionary<string, XElement>();

            // First we build a list of all available commands,
            // including their parameters and return types.
            var commands = new SortedDictionary<string, XElement>();
            foreach (var command in input.Root.Elements("commands").Elements("command"))
            {
                commands.Add(FunctionName(command), command);
            }

            // First, we go through all available features and extensions and build a list
            // of commands that belong to them.
            // We will then use this information to "decorate" our bindings with
            // information about versioning, extension support and deprecation.
            foreach (var feature in features.Concat(extensions))
            {
                var category = TrimName(feature.Attribute("name").Value);
                var extension =
                    feature.Name == "extension" ? category.Substring(0, category.IndexOf("_")) : "Core";
                var apinames =
                    (feature.Attribute("api") != null ? feature.Attribute("api").Value :
                    feature.Attribute("supported") != null ? feature.Attribute("supported").Value :
                    "gl").Split('|');
                var version =
                    (feature.Attribute("number") != null ? feature.Attribute("number").Value : "")
                    .Split('|');

                int i = -1;
                foreach (var apiname in apinames)
                {
                    i++;
                    if (!APIs.ContainsKey(apiname))
                        APIs.Add(apiname, new XElement("api", new XAttribute("name", apiname)));
                    var api = APIs[apiname];

                    var cmd_category = category;
                    var cmd_extension = extension;
                    var cmd_version = version.Length > i ? version[i] : version[0];

                    foreach (var command in feature.Elements("require").Elements("command"))
                    {
                        var cmd_name = TrimName(command.Attribute("name").Value);

                        XElement function = TranslateCommand(commands[cmd_name]);
                        function.Add(new XAttribute("category", cmd_category));
                        function.Add(new XAttribute("extension", cmd_extension));
                        if (!String.IsNullOrEmpty(cmd_version))
                            function.Add(new XAttribute("version", cmd_version));

                        api.Add(function);
                    }

                    // Mark all deprecated functions as such
                    foreach (var command in feature.Elements("remove").Elements("command"))
                    {
                        var deprecated_name = TrimName(command.Attribute("name").Value);
                        var function =
                            api.Elements("function")
                            .FirstOrDefault(t => t.Attribute("name").Value == deprecated_name);

                        if (function != null)
                        {
                            function.Add(new XAttribute("deprecated", cmd_version));
                        }
                    }
                }
            }

            return APIs.Values;
        }

        private XElement TranslateCommand(XElement command)
        {
            XElement function = new XElement("function");

            var cmd_name = FunctionName(command);
            var name = new XAttribute("name", cmd_name);

            var returns = new XElement(
                "returns",
                new XAttribute(
                    "type",
                    FunctionParameterType(command.Element("proto"))
                        .Replace("const", String.Empty)
                        .Replace("struct", String.Empty)
                        .Trim()));

            foreach (var parameter in command.Elements("param"))
            {
                var param = FunctionParameterType(parameter);

                var p = new XElement("param");
                var pname = new XAttribute("name", parameter.Element("name").Value);
                var type = new XAttribute(
                    "type",
                    param
                        .Replace("const", String.Empty)
                        .Replace("struct", String.Empty)
                        .Trim());

                var count = parameter.Attribute("len") != null ?
                    new XAttribute("count", parameter.Attribute("len").Value) : null;

                var flow = new XAttribute("flow",
                    param.Contains("*") && !param.Contains("const") ? "out" : "in");

                p.Add(pname, type, flow);
                if (count != null)
                {
                    p.Add(count);
                }

                function.Add(p);
            }

            function.Add(name);
            function.Add(returns);
            return function;
        }

        string FunctionName(XElement e)
        {
            return TrimName(e.Element("proto").Element("name").Value);
        }

        string FunctionParameterType(XElement e)
        {
            // Parse the C-like <proto> element. Possible instances:
            // Return types:
            // - <proto>void <name>glGetSharpenTexFuncSGIS</name></proto>
            //   -> <returns>void</returns>
            // - <proto group="String">const <ptype>GLubyte</ptype> *<name>glGetString</name></proto>
            //   -> <returns>String</returns> 
            // Note: group attribute takes precedence if it exists. This matches the old .spec file format.
            // Parameter types:
            // - <param><ptype>GLenum</ptype> <name>shadertype</name></param>
            //   -> <param name="shadertype" type="GLenum" />
            // - <param len="1"><ptype>GLsizei</ptype> *<name>length</name></param>
            //   -> <param name="length" type="GLsizei" count="1" />

            var proto = e.Value;
            var name = e.Element("name").Value;
            var group = e.Attribute("group");

            var ret = proto.Remove(proto.LastIndexOf(name)).Trim();

            if (group != null)
            {
                var words = ret.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                if (words[0] == "struct" || words[0] == "const")
                    words[1] = group.Value;
                else
                    words[0] = group.Value;

                ret = String.Join(" ", words);
            }

            return ret;
        }

        string TrimName(string name)
        {
            if (name.StartsWith(EnumPrefix))
                return name.Remove(0, EnumPrefix.Length);
            else if (name.StartsWith(FuncPrefix))
                return name.Remove(0, FuncPrefix.Length);
            else
                return name;
        }

        static string Join(string left, string right)
        {
            if (!String.IsNullOrEmpty(left) && !String.IsNullOrEmpty(right))
                return left + "|" + right;
            else if (!String.IsNullOrEmpty(left))
                return left;
            else if (!String.IsNullOrEmpty(right))
                return right;
            else
                return String.Empty;
        }

        static XAttribute Lookup(IDictionary<string, XElement> categories, string cmd_name, string attribute)
        {
            if (categories.ContainsKey(cmd_name))
            {
                return categories[cmd_name].Attribute(attribute);
            }
            return null;
        }
    }
}

