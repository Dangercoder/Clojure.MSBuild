using System;
using System.Collections.Generic;
using System.Linq;

namespace ClojureApp.Services
{
    public class DataProcessor
    {
        private readonly string _name;
        private int _processedCount;
        
        public DataProcessor(string name)
        {
            _name = name;
            _processedCount = 0;
        }
        
        // Property
        public string Name => _name;
        public int ProcessedCount => _processedCount;
        
        // Instance method
        public string ProcessData(string input)
        {
            _processedCount++;
            return $"[{_name}] Processed: {input.ToUpper()} (#{_processedCount})";
        }
        
        // Static method
        public static double CalculateAverage(List<int> numbers)
        {
            if (numbers == null || numbers.Count == 0)
                return 0;
            return numbers.Average();
        }
        
        // Method that returns a complex object
        public Dictionary<string, object> GetStatus()
        {
            return new Dictionary<string, object>
            {
                ["processor"] = _name,
                ["count"] = _processedCount,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["isActive"] = true
            };
        }
    }
    
    // Static utility class
    public static class MathUtils
    {
        public static int Add(int a, int b) => a + b;
        
        public static int Multiply(int a, int b) => a * b;
        
        public static double Power(double baseNum, double exponent) 
            => Math.Pow(baseNum, exponent);
        
        public static string FormatNumber(double number, int decimals = 2)
            => number.ToString($"F{decimals}");
    }
    
    // A simple data class
    public class Person
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Age { get; set; }
        
        public Person(string firstName, string lastName, int age)
        {
            FirstName = firstName;
            LastName = lastName;
            Age = age;
        }
        
        public string GetFullName() => $"{FirstName} {LastName}";
        
        public override string ToString() 
            => $"Person: {GetFullName()}, Age: {Age}";
    }
}