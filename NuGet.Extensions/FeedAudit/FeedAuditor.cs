using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NuGet.Extensions.FeedAudit
{
    /// <summary>
    /// Allows assembly and package dependency auditing for NuGet feeds
    /// </summary>
    public class FeedAuditor
    {
        public delegate void PackageAuditEventHandler(object sender, PackageAuditEventArgs args);
        private readonly IPackageRepository _packageRepository;
        private readonly List<string> _exceptions; 
        private List<FeedAuditResult> _results = new List<FeedAuditResult>();
        private bool _unlisted;
        private List<IPackage> _packages;

        public event PackageAuditEventHandler StartPackageAudit = delegate { };
        public event PackageAuditEventHandler FinishedPackageAudit = delegate { };
        public event EventHandler StartPackageListDownload = delegate { };
        public event EventHandler FinishedPackageListDownload = delegate { };
 
        public List<FeedAuditResult> AuditResults
        {
            get { return _results; }
            set { _results = value; }
        }

        public FeedAuditor(IPackageRepository packageRepository, IEnumerable<String> exceptions, Boolean unlisted)
        {
            _packageRepository = packageRepository;
            _exceptions = exceptions.ToList();
            _unlisted = unlisted;
        }

        /// <summary>
        /// Audits a feed and provides back a set of results
        /// </summary>
        /// <returns></returns>
        public void Audit(IPackage packageToAudit = null)
        {
            if (packageToAudit == null)
            {
                StartPackageListDownload(this, new EventArgs());
                _packages = _packageRepository.GetPackages().Where(p => p.IsLatestVersion).OrderBy(p => p.Id).ToList();
                FinishedPackageListDownload(this, new EventArgs());
            }
            else
            {
                _packages = new List<IPackage>(new[] {packageToAudit});
            }

            foreach (var package in _packages)
            {
                //OData wont let us query this remotely (again, fuck OData).
                if (_unlisted == false && package.Listed == false) continue;
                
                StartPackageAudit(this, new PackageAuditEventArgs(){Package = package});

                //Try the next one if we are using this one as an exception
                //TODO Wildcards would be great!
                if (_exceptions.Any(e => e.Equals(package.Id,StringComparison.OrdinalIgnoreCase)))
                    continue;

                var currentResult = new FeedAuditResult {Package = package};
                var actualAssemblyReferences = GetPackageAssemblyReferenceList(package, currentResult);

                //Prune dependency list based on additional assemblies included in the package...
                actualAssemblyReferences = RemoveInternallySatisfiedDependencies(actualAssemblyReferences, package);

                var packageDependencies = GetDependencyAssemblyList(package, currentResult).ToList();

                var usedDependencies = new List<IPackage>();
                foreach (var actualDependency in actualAssemblyReferences)
                {
                    var possibles = packageDependencies.Where(d => d.GetFiles().Any(a => new FileInfo(a.Path).Name.Equals(actualDependency.Name + ".dll", StringComparison.OrdinalIgnoreCase)));
                    usedDependencies.AddRange(possibles.Select(p => p));
                    if (!possibles.Any())
                        currentResult.UnresolvedAssemblyReferences.Add(actualDependency);
                    else
                        currentResult.ResolvedAssemblyReferences.Add(actualDependency);
                }

                currentResult.UsedPackageDependencies.AddRange(packageDependencies.Where(usedDependencies.Contains).Select(l => l));
                currentResult.UnusedPackageDependencies.AddRange(packageDependencies.Where(p => !usedDependencies.Contains(p)).Select(l => l));
                AuditResults.Add(currentResult);
                FinishedPackageAudit(this, new PackageAuditEventArgs{Package = package});
            }
        }

        private static IEnumerable<AssemblyName> RemoveInternallySatisfiedDependencies(IEnumerable<AssemblyName> actualAssemblyReferences, IPackage package)
        {
            var fileNames = package.GetFiles().Select(f => new FileInfo(f.Path).Name).ToList();
            return actualAssemblyReferences.Where(a => !fileNames.Contains(a.Name + ".dll") && !fileNames.Contains(a.Name + ".exe"));
        }

        /// <summary>
        /// Returns a dictionary mapping of IPackages to their included files.
        /// </summary>
        /// <param name="package"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        private IEnumerable<IPackage> GetDependencyAssemblyList(IPackage package, FeedAuditResult result)
        {
            var packageDependencies = new List<IPackage>();
            foreach (var dependency in package.Dependencies)
            {
                //HACK Slow and wrong and evil and I HATE ODATA.
                var dependencyPackage = _packages.FirstOrDefault(p => p.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase));
                if (dependencyPackage == null)
                {
                    result.UnresolvedDependencies.Add(dependency);
                    continue;
                }
                packageDependencies.Add(dependencyPackage);
            }
            return packageDependencies;
        }

        /// <summary>
        /// Gets a list of AssemblyNames referenced by the files in a package
        /// </summary>
        /// <param name="package">The package to check.</param>
        /// <param name="result">A result to append errors to.</param>
        /// <returns></returns>
        private static IEnumerable<AssemblyName> GetPackageAssemblyReferenceList(IPackage package, FeedAuditResult result)
        {
            var actualDependencies = new List<AssemblyName>();
            foreach (var file in package.GetFiles().Where(f => f.Path.EndsWith(".dll") || f.Path.EndsWith("*.exe")))
            {
                using (var stream = file.GetStream())
                {
                    try
                    {
                        var assembly = Assembly.Load(stream.ReadAllBytes());
                        actualDependencies.AddRange(assembly.GetReferencedAssemblies());
                    }
                    catch (Exception)
                    {
                        result.UnloadablePackageFiles.Add(file.Path);
                    }
                }
            }
            return actualDependencies.Where(d => !IsProbablySystemAssembly(d)).Distinct(new AssemblyNameEqualityComparer());
        }

        /// <summary>
        /// Checks whether an assembly name is probably a system (ie GAC) assembly.  Not infallible.
        /// </summary>
        /// <param name="d"></param>
        /// <returns></returns>
        private static bool IsProbablySystemAssembly(AssemblyName d)
        {
            return d.Name.StartsWith("System.") || d.Name.Equals("System") || d.Name.Equals("mscorlib");
        }
    }
}