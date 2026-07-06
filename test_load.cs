using System;
using System.Reflection;
class Program {
    static void Main() {
        try {
            var asm = Assembly.Load("StbImageSharp");
            Console.WriteLine("Loaded: " + asm.FullName);
        } catch (Exception ex) {
            Console.WriteLine(ex);
        }
    }
}
