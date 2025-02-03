using System;
using System.IO;
using System.Xml.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

public static class Utility
{
    public static double ParseDouble(string s)
    {
        if (double.TryParse(s, out double result))
        {
            return result;
        }
        else
        {
            return 0;
        }
    }

}

public class FossilEmissionRecord
{
    public string GeneratorName { get; set; }
    public DateTime Date { get; set; }
    public double Emission { get; set; }
}

public interface IGenerator
{
    string GeneratorName { get; }
    double ProcessGenerator(XElement generatorElement,
                            Dictionary<string, (double valueFactor, double emissionFactor)> referenceData,
                            List<FossilEmissionRecord> records,
                            XElement actualHeatRates);
}

public class WindGeneratorProcessor : IGenerator
{
    public string GeneratorName { get; } = "WindGenerator";
    public double ProcessGenerator(XElement generatorElement,
                                   Dictionary<string, (double valueFactor, double emissionFactor)> referenceData,
                                   List<FossilEmissionRecord> records,
                                   XElement actualHeatRates)
    {
        string genName = generatorElement.Element("Name").Value;
        XElement generation = generatorElement.Element("Generation");
        double total = 0;
        foreach (var day in generation.Elements("Day"))
        {
            double energy = Utility.ParseDouble(day.Element("Energy").Value);
            double price = Utility.ParseDouble(day.Element("Price").Value);
            total += energy * price * referenceData[genName].valueFactor;
        }
        return total;
    }
}

public class GasGeneratorProcessor : IGenerator
{
    public string GeneratorName { get; } = "GasGenerator";
    public double ProcessGenerator(XElement generatorElement,
                                   Dictionary<string, (double valueFactor, double emissionFactor)> referenceData,
                                   List<FossilEmissionRecord> records,
                                   XElement actualHeatRates)
    {
        string genName = generatorElement.Element("Name").Value;
        XElement generation = generatorElement.Element("Generation");
        double total = 0;
        double emissionsRating = Utility.ParseDouble(generatorElement.Element("EmissionsRating").Value);
        double emissionFactor = referenceData[genName].emissionFactor;
        foreach (var day in generation.Elements("Day"))
        {
            double energy = Utility.ParseDouble(day.Element("Energy").Value);
            double price = Utility.ParseDouble(day.Element("Price").Value);
            double dayValue = energy * price * referenceData[genName].valueFactor;
            total += dayValue;
            double dailyEmissions = energy * emissionsRating * emissionFactor;
            records.Add(new FossilEmissionRecord
            {
                GeneratorName = genName,
                Date = DateTime.Parse(day.Element("Date").Value),
                Emission = dailyEmissions
            });
        }
        return total;
    }

}

public class CoalGeneratorProcessor : IGenerator
{
    public string GeneratorName { get; } = "CoalGenerator";

    public double ProcessGenerator(XElement generatorElement,
                                   Dictionary<string, (double valueFactor, double emissionFactor)> referenceData,
                                   List<FossilEmissionRecord> records,
                                   XElement actualHeatRates)
    {
        string genName = generatorElement.Element("Name").Value;
        XElement generation = generatorElement.Element("Generation");
        double totalHeatInput = Utility.ParseDouble(generatorElement.Element("TotalHeatInput").Value);
        double actualNetGeneration = Utility.ParseDouble(generatorElement.Element("ActualNetGeneration").Value);
        double emissionsRating = Utility.ParseDouble(generatorElement.Element("EmissionsRating").Value);
        double emissionFactor = referenceData[genName].emissionFactor;
        double actualHeatRate;

        if (actualNetGeneration != 0)
        {
            actualHeatRate = totalHeatInput / actualNetGeneration;
        }
        else
        {
            actualHeatRate = 0;
        }

        actualHeatRates.Add(new XElement("ActualHeatRate",
            new XElement("Name", genName),
            new XElement("HeatRate", actualHeatRate)
        ));
        double total = 0;
        foreach (var day in generation.Elements("Day"))
        {
            double energy = Utility.ParseDouble(day.Element("Energy").Value);
            double price = Utility.ParseDouble(day.Element("Price").Value);
            double dayValue = energy * price * referenceData[genName].valueFactor;
            total += dayValue;
            double dailyEmissions = energy * emissionsRating * emissionFactor;
            records.Add(new FossilEmissionRecord
            {
                GeneratorName = genName,
                Date = DateTime.Parse(day.Element("Date").Value),
                Emission = dailyEmissions
            });
        }
        return total;
    }
}

public static class FactoryDP
{
    public static IGenerator GetProcessor(string elementName)
    {
        switch (elementName)
        {
            case "WindGenerator":
                return new WindGeneratorProcessor();
            case "GasGenerator":
                return new GasGeneratorProcessor();
            case "CoalGenerator":
                return new CoalGeneratorProcessor();
            default:
                return null;
        }
    }
}

public static class App
{
    private static string inputDir;
    private static string outputDir;
    private static string referenceDataXmlPath;
    private static IConfiguration Configuration;

    static App()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        Configuration = builder.Build();

        inputDir = Configuration["InputFolder"];
        outputDir = Configuration["OutputFolder"];
        referenceDataXmlPath = Configuration["ReferenceDataXmlPath"];

        // Log the paths being used
        Console.WriteLine($"Input Folder: {inputDir}");
        Console.WriteLine($"Output Folder: {outputDir}");
        Console.WriteLine($"Reference Data XML Path: {referenceDataXmlPath}");
    }

    private static Dictionary<string, (double valueFactor, double emissionFactor)> LoadReferenceData()
    {
        var referenceData = new Dictionary<string, (double valueFactor, double emissionFactor)>();

        XElement refDataXml = XElement.Load(referenceDataXmlPath);

        var valueFactorsElement = refDataXml.Descendants("ValueFactor").FirstOrDefault();
        var emissionFactorsElement = refDataXml.Descendants("EmissionsFactor").FirstOrDefault();

        double offshoreWindValueFactor = double.Parse(valueFactorsElement.Element("Low").Value ?? "0");
        referenceData.Add("Wind[Offshore]", (offshoreWindValueFactor, 0));

        double onshoreWindValueFactor = double.Parse(valueFactorsElement.Element("High").Value ?? "0");
        referenceData.Add("Wind[Onshore]", (onshoreWindValueFactor, 0));

        double gasValueFactor = double.Parse(valueFactorsElement.Element("Medium").Value ?? "0");
        double gasEmissionFactor = double.Parse(emissionFactorsElement.Element("Medium").Value ?? "0");
        referenceData.Add("Gas[1]", (gasValueFactor, gasEmissionFactor));

        double coalValueFactor = double.Parse(valueFactorsElement.Element("Medium").Value ?? "0");
        double coalEmissionFactor = double.Parse(emissionFactorsElement.Element("High").Value ?? "0");
        referenceData.Add("Coal[1]", (coalValueFactor, coalEmissionFactor));

        Console.WriteLine("Loaded reference data.");
        return referenceData;
    }


    private static void ProcessInput(string inputFileName)
    {
        Console.WriteLine($"Processing file: {inputFileName}");

        XElement inputXml = XElement.Load(Path.Combine(inputDir, inputFileName));
        var referenceData = LoadReferenceData();
        var result = new XElement("GenerationOutput");
        var totalsElement = new XElement("Totals");
        var actualHeatRates = new XElement("ActualHeatRates");
        var records = new List<FossilEmissionRecord>();

        foreach (string parentName in new[] { "Wind", "Gas", "Coal" })
        {
            foreach (var generatorElement in inputXml.Descendants(parentName).Elements())
            {
                var processor = FactoryDP.GetProcessor(generatorElement.Name.LocalName);
                if (processor == null) continue;

                double total = processor.ProcessGenerator(generatorElement, referenceData, records, actualHeatRates);
                string genName = generatorElement.Element("Name").Value;

                totalsElement.Add(new XElement("Generator",
                    new XElement("Name", genName),
                    new XElement("Total", total)
                ));
            }
        }

        var groupedRecords = records
            .GroupBy(record => record.Date)
            .OrderBy(group => group.Key);

        var maxEmissionGenerators = new XElement("MaxEmissionGenerators");

        foreach (var group in groupedRecords)
        {
            var maxRecord = group.OrderByDescending(r => r.Emission).First();

            var dayElement = new XElement("Day",
                new XElement("Name", maxRecord.GeneratorName),
                new XElement("Date", maxRecord.Date.ToString("o")),
                new XElement("Emission", maxRecord.Emission)
            );

            maxEmissionGenerators.Add(dayElement);
        }


        result.Add(totalsElement);
        result.Add(maxEmissionGenerators);
        result.Add(actualHeatRates);
        result.Save(Path.Combine(outputDir, $"{inputFileName.Replace(".xml", "-Result.xml")}"));

        Console.WriteLine($"Processed {inputFileName} and saved results.");
    }

    public static void Main()
    {
        Console.WriteLine("Starting the processing...");

        if (Directory.Exists(inputDir))
        {
            var files = Directory.GetFiles(inputDir, "*.xml");
            if (files.Length == 0)
            {
                Console.WriteLine("No XML files found in the input folder.");
            }
            else
            {
                foreach (var file in files)
                {
                    ProcessInput(Path.GetFileName(file));
                }
            }
        }
        else
        {
            Console.WriteLine("Input folder does not exist.");
        }

        Console.WriteLine("Processing completed.");
    }
}