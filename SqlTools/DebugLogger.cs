using Caliburn.Micro;
using System;
using System.Diagnostics;

namespace SqlTools
{
    internal class DebugLogger : ILog
    {
        private readonly Type _type;

        public DebugLogger(Type type)
        {
            _type = type;
        }

        public void Error(Exception exception)
        {
            Debug.WriteLine(CreateLogMessage(exception.ToString()), "ERROR");
        }

        public void Info(string format, params object[] args)
        {
            Debug.WriteLine(CreateLogMessage(format, args), "INFO");
        }

        public void Warn(string format, params object[] args)
        {
            Debug.WriteLine(CreateLogMessage(format, args), "WARN");
        }

        private string CreateLogMessage(string format, params object[] args)
        {
            return string.Format(@"«cal»[{0:HH\:MM\:ss.fff}] {1}", DateTime.Now, string.Format(format, args));
        }
    }
}