using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EnvDTE;
using Microsoft.VisualStudio.TemplateWizard;

namespace VisualSwift
{
    public sealed class SwiftTemplateWizard : IWizard
    {
        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
        {
            var root = Path.GetFullPath(replacementsDictionary["$destinationdirectory$"]);
            var templatePath = customParams.Length > 0 ? customParams[0] as string : null;
            var library = string.Equals(Path.GetFileName(templatePath), "SwiftLibrary.vstemplate", StringComparison.OrdinalIgnoreCase);
            var cxx = string.Equals(Path.GetFileName(templatePath), "SwiftCxx.vstemplate", StringComparison.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>
            {
                ["VisualSwift\\Invoke-Swift.ps1"] = "VisualSwift.Templates.Invoke-Swift.ps1",
                ["VisualSwift\\WindowsSwiftEnvironment.ps1"] = "VisualSwift.Templates.WindowsSwiftEnvironment.ps1"
            };
            if (library)
            {
                files["Sources\\Library\\Greeter.swift"] = "VisualSwift.Templates.Library.Greeter.swift";
                files["Tests\\LibraryTests\\GreeterTests.swift"] = "VisualSwift.Templates.Library.GreeterTests.swift";
            }
            else if (cxx)
            {
                files["Sources\\App\\main.swift"] = "VisualSwift.Templates.Cxx.main.swift";
                files["Sources\\CppMath\\CppMath.cpp"] = "VisualSwift.Templates.Cxx.CppMath.cpp";
                files["Sources\\CppMath\\include\\CppMath.h"] = "VisualSwift.Templates.Cxx.CppMath.h";
            }
            else
            {
                if (!string.Equals(Path.GetFileName(templatePath), "SwiftConsole.vstemplate", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Swift project template could not be identified.");
                files["Sources\\App\\main.swift"] = "VisualSwift.Templates.Console.main.swift";
            }
            foreach (var file in files)
                if (File.Exists(Path.Combine(root, file.Key)))
                    throw new InvalidOperationException("A Swift template file already exists in the destination: " + file.Key);
            foreach (var file in files)
            {
                var target = Path.Combine(root, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(file.Value))
                {
                    if (source == null) throw new InvalidOperationException("A Swift template resource is missing: " + file.Value);
                    using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        source.CopyTo(destination);
                }
            }
            if (!library && !File.Exists(SwiftDebugOptions.FilePath(root)))
                new SwiftDebugOptions { Product = replacementsDictionary["$safeprojectname$"], UseF5 = true }.Save(root);
        }

        public bool ShouldAddProjectItem(string filePath) => true;
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void ProjectFinishedGenerating(Project project) { }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public void RunFinished() { }
    }
}
