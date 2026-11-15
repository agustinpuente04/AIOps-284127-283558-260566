using ExportationModel.ExportDomain;

namespace PharmaGo.PharmacyService.Models.In.Exports
{
    public class DrugsExportationModel
    {
        public string FormatName { get; set; }
        public IEnumerable<Parameter> Parameters { get; set; }
    }
}
