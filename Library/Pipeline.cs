﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BlendInteractive.Denina.Core.Documentation;
using DeninaSharp.Core.Configuration;
using DeninaSharp.Core.Documentation;

namespace DeninaSharp.Core
{
    public partial class Pipeline
    {
        // Some magic strings
        public const string GLOBAL_VARIABLE_NAME = "__global";
        public const string WRITE_TO_VARIABLE_COMMAND = "core.writeto";
        public const string READ_FROM_VARIABLE_COMMAND = "core.readfrom";
        public const string LABEL_COMMAND = "core.label";
        public const string FINAL_COMMAND_LABEL = "end";

        public string NextCommandLabel { get; set; }

        public static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(); // This is just to keep them handy for the documentor

        private static readonly Dictionary<string, MethodInfo> commandMethods = new Dictionary<string, MethodInfo>();

        private Stopwatch timer = new Stopwatch();

        private readonly List<PipelineCommand> commands = new List<PipelineCommand>();

        private Dictionary<string, PipelineVariable> variables = new Dictionary<string, PipelineVariable>();
        private static Dictionary<string, PipelineVariable> globalVariables = new Dictionary<string, PipelineVariable>();

        public Pipeline(string commandString = null)
        {
            // Add this assembly to initialze the filters
            AddAssembly(Assembly.GetExecutingAssembly());

            if (!String.IsNullOrWhiteSpace(commandString))
            {
                AddCommand(commandString);
            }

            var configSection = (PipelineConfigSection) ConfigurationManager.GetSection("denina");
            if (configSection != null)
            {
                foreach (PipelineConfigVariable configVariable in configSection.Variables)
                {
                    Pipeline.SetGlobalVariable(configVariable.Key, configVariable.Value, true);
                }
            }
        }

        public static ReadOnlyDictionary<string, MethodInfo> CommandMethods
        {
            get { return new ReadOnlyDictionary<string, MethodInfo>(commandMethods); }
        }

        public ReadOnlyCollection<PipelineCommand> Commands
        {
            get { return commands.AsReadOnly(); }
        }

        public ReadOnlyDictionary<string, PipelineVariable> Variables
        {
            get { return new ReadOnlyDictionary<string, PipelineVariable>(variables); }
        }

        public static ReadOnlyDictionary<string, PipelineVariable> GlobalVariables
        {
            get { return new ReadOnlyDictionary<string, PipelineVariable>(globalVariables); }
        }

        
        public static void AddAssembly(Assembly assembly)
        {
            // Iterate all the classes in this assembly
            foreach (Type thisType in assembly.GetTypes())
            {
                // Does this assembly have the TextFilters attribute?
                if (thisType.GetCustomAttributes(typeof (FiltersAttribute), true).Any())
                {
                    // Process It
                    AddType(thisType);
                }
            }
        }

        public static void AddType(Type type, string category = null)
        {
            if (category == null)
            {
                if (!type.GetCustomAttributes(typeof (FiltersAttribute), true).Any())
                {
                    throw new Exception("Type does not have a TextFilters attribute. In this case, you must pass a category name into AddType.");
                }
                category = ((FiltersAttribute) type.GetCustomAttributes(typeof (FiltersAttribute), true).First()).Category;
            }

            foreach (var method in type.GetMethods().Where(m => m.GetCustomAttributes(typeof (FilterAttribute), true).Any()))
            {
                string name = ((FilterAttribute) method.GetCustomAttributes(typeof (FilterAttribute), true).First()).Name;
                AddMethod(method, category, name);
            }
        }

        public static void AddMethod(MethodInfo method, string category, string name = null)
        {
            // Check if it has any requirements
            foreach (RequiresAttribute dependency in method.GetCustomAttributes(typeof (RequiresAttribute), true))
            {
                if (Type.GetType(dependency.TypeName) == null)
                {
                    return;
                }
            }

            if (name == null)
            {
                name = ((FilterAttribute) method.GetCustomAttributes(typeof (FilterAttribute), true).First()).Name;
            }

            var fullyQualifiedFilterName = String.Concat(category.ToLower(), ".", name.ToLower());
            
            commandMethods.Remove(fullyQualifiedFilterName); // Remove it if it exists already                  
            commandMethods.Add(fullyQualifiedFilterName, method);
        }

        public string Execute(string input = null)
        {
            // Add a pass-through command at the end just to hold a label called "end".
            if (commands.Any(c => c.Label == FINAL_COMMAND_LABEL))
            {
                commands.Remove(commands.First(c => c.Label == FINAL_COMMAND_LABEL));
            }
            commands.Add(
                new PipelineCommand()
                {
                    CommandName = LABEL_COMMAND,
                    Label = FINAL_COMMAND_LABEL,
                    CommandArgs = new Dictionary<object, string>() { { 0, FINAL_COMMAND_LABEL } }
                }
            );

            // We set the global variable to the incoming string. It will be modified and eventually returned from this variable slot.
            SetVariable(GLOBAL_VARIABLE_NAME, input);

            // We're going to set up a linked list of commands. Each command holds a reference to the next command in its SendToLabel property. The last command is NULL.
            var commandQueue = new Dictionary<string, PipelineCommand>();
            for (var index = 0; index < commands.Count; index++)
            {
                var thisCommand = commands[index];

                // If this is a "Label" command, then we need to get the label "out" to a property
                if (thisCommand.NormalizedCommandName == LABEL_COMMAND)
                {
                    thisCommand.Label = thisCommand.DefaultArgument;
                }
                
                // If (1) this command doesn't already have a SendToLabel (it...shouldn't...I don't think), and (2) we're not on the last command, then set the SendToLabel of this command to the Label of the next command
                if (thisCommand.SendToLabel == null && index < commands.Count - 1)
                {
                    thisCommand.SendToLabel = commands[index + 1].Label;
                }

                // Add this command to the queue, keyed to its Label
                commandQueue.Add(thisCommand.Label.ToLower(), thisCommand);
            }

            // We're going to stay in this loop, resetting "command" each iteration, until SendToLabel is NULL
            NextCommandLabel = commandQueue.First().Value.Label;
            while(true)
            {
                // Do we have a next command?
                if (NextCommandLabel == null)
                {
                    // Stick a fork in us, we're done
                    break;
                }


                // Does the specified next command exist?
                if (!commandQueue.ContainsKey(NextCommandLabel.ToLower()))
                {
                    throw new Exception(String.Format("Specified command label \"{0}\" does not exist in the command queue.", NextCommandLabel));
                }

                // Get the next command
                var command = commandQueue[NextCommandLabel.ToLower()];

                // Are we writing to a variable?
                if (command.NormalizedCommandName == WRITE_TO_VARIABLE_COMMAND)
                {
                    // Get the active text and copy it to a different variable
                    SetVariable(command.OutputVariable, GetVariable(GLOBAL_VARIABLE_NAME));
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }

                // Are we reading from a variable?
                if (command.NormalizedCommandName == READ_FROM_VARIABLE_COMMAND)
                {
                    // Get the variable and copy it into the active text
                    SetVariable(GLOBAL_VARIABLE_NAME, GetVariable(command.InputVariable));
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }

                // Is this a label?
                if (command.NormalizedCommandName == LABEL_COMMAND)
                {
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }
                
                // Note that the above commands will never actually execute. This is why their methods are just empty shells...

                // Do we a method for this command?
                if (!CommandMethods.ContainsKey(command.NormalizedCommandName))
                {
                    throw new DeninaException("No command method found for \"" + command.CommandName + "\"");
                }

                // Set a pipeline reference which can be accessed inside the filter method
                command.Pipeline = this;

                // Resolve any arguments that are actually variable names
                command.ResolveArguments();

                // Execute
                var method = CommandMethods[command.NormalizedCommandName];
                try
                {
                    timer.Reset();
                    timer.Start();

                    // This is where we make the actual method call. We get the text out of the InputVariable slot, and we put it back into the OutputVariable slot. (These are usually the same slot...)
                    // We're going to "SafeSet" this, so they can't pipe output to a read-only variable
                    var output = method.Invoke(null, new[] {GetVariable(command.InputVariable), command});
                    SafeSetVariable(command.OutputVariable, output);

                    command.ElapsedTime = timer.ElapsedMilliseconds;
                }
                catch (Exception e)
                {
                    // Since this was reflected, the "outer" exception is "an exception was thrown by the target of an invocation"
                    // Hence, the "real" exception is the inner exception
                    var exception = (DeninaException)e.InnerException;

                    exception.CurrentCommandText = command.OriginalText;
                    exception.CurrentCommandName = command.NormalizedCommandName;
                    throw exception;
                }

                // Set the pointer to the next command
                NextCommandLabel = command.SendToLabel;
            }

            // Return what's in the global variable            
            return GetVariable(GLOBAL_VARIABLE_NAME).ToString();

        }

        public bool IsSet(string key)
        {
            return variables.ContainsKey(key);
        }

        public static bool IsSetGlobally(string key)
        {
            return globalVariables.ContainsKey(key);
        }

        public object GetVariable(string key, bool checkGlobal = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);

            if (!variables.ContainsKey(key))
            {
                if (checkGlobal)
                {
                    if (IsSetGlobally(key))
                    {
                        return GetGlobalVariable(key);
                    }
                }

                throw new DeninaException(String.Format("Attempt to access non-existent variable: \"{0}\"", key));
            }

            return variables[PipelineCommandParser.NormalizeVariableName(key)].Value ?? String.Empty;
        }

        public static object GetGlobalVariable(string key)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);

            if (!globalVariables.ContainsKey(key))
            {
                throw new DeninaException(String.Format("Attempt to access non-existent variable: \"{0}\"", key));
            }

            return globalVariables[PipelineCommandParser.NormalizeVariableName(key)].Value;           
        }

        public void SetVariable(string key, object value, bool readOnly = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);
            variables.Remove(key);
            variables.Add(
                key,
                new PipelineVariable(
                    key,
                    value,
                    readOnly
                    )
            );
        }

        public static void SetGlobalVariable(string key, object value, bool readOnly = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);
            globalVariables.Remove(key);
            globalVariables.Add(
                key,
                new PipelineVariable(
                    key,
                    value,
                    readOnly
                    )
            );
        }

        public static void ClearGlobalVariables()
        {
            globalVariables.Clear();
        }

        // This will refuse to set variables flagged as read-only
        public void SafeSetVariable(string key, object value, bool readOnly = false)
        {
            // Do we have a variable with the same name that's readonly?
            if (variables.Any(v => v.Value.Name == key && v.Value.ReadOnly))
            {
                throw new DeninaException(String.Format("Attempt to reset value of read-only variable \"{0}\"", key));
            }

            SetVariable(key, value, readOnly);
        }

        public void AddCommand(PipelineCommand command)
        {
            commands.Add(command);
        }

        public void AddCommand(string commandString)
        {
            commands.AddRange(PipelineCommandParser.ParseCommandString(commandString));
        }

        public void AddCommand(string commandName, Dictionary<object, string> commandArgs)
        {
            var command = new PipelineCommand
            {
                CommandName = commandName,
                CommandArgs = commandArgs
            };
            commands.Add(command);
        }
    }
}