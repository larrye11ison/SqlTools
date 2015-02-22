namespace SqlTools.Shell
{
    /// <summary>
    /// One instance of text found within a scripted object's source text.
    /// </summary>
    public class FoundTextResult
    {
        /// <summary>
        /// Initializes a new instance of the FoundTextResult class.
        /// </summary>
        public FoundTextResult(int index)
        {
            Index = index;
        }

        public int EndOffset { get; set; }

        public int Index { get; private set; }

        public int StartOffset { get; set; }

        public override string ToString()
        {
            return string.Format("From {0} to {1}", StartOffset, EndOffset);
        }
    }
}