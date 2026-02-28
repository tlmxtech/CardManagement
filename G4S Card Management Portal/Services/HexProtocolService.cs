using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CardManagement.Services
{
    public class HexProtocolService
    {
        public List<string> GenerateTopflyHex(string imei, string actionType, List<string> cardTags)
        {
            var hexCards = new List<string>();
            string formattedImei = FormatImei(imei);

            if (actionType == "Replace")
                hexCards.Add(BuildTopflyCommand(formattedImei, "NFCIDD,A#"));

            foreach (var tag in cardTags)
            {
                string msgData = actionType == "Remove" ? $"NFCIDD,{tag}#" : $"NFCIDA,{tag}#";
                hexCards.Add(BuildTopflyCommand(formattedImei, msgData));
            }
            return hexCards;
        }

        public List<string> GenerateJointechHex(string actionType, List<string> cardTags)
        {
            var hexCards = new List<string>();
            if (actionType == "Replace") hexCards.Add(TextToHexSpaces("(P41,1,3)"));

            var batches = cardTags.Select((x, i) => new { Index = i, Value = x })
                                  .GroupBy(x => x.Index / 10)
                                  .Select(x => x.Select(v => v.Value).ToList());

            foreach (var batch in batches)
            {
                string command = actionType == "Remove"
                    ? $"(P41,1,2,{batch.Count},{string.Join(",", batch)})"
                    : $"(P41,1,1,{batch.Count},{string.Join(",", batch)})";
                hexCards.Add(TextToHexSpaces(command));
            }
            return hexCards;
        }

        private string BuildTopflyCommand(string imei, string messageData)
        {
            string hexMessageData = TextToHexSpaces(messageData);
            string hexLength = (Encoding.ASCII.GetBytes(messageData).Length - 1).ToString("X2");
            return $"27 27 81 00 {hexLength} 00 01 {imei} 01 {hexMessageData}";
        }

        private string FormatImei(string imei)
        {
            if (string.IsNullOrEmpty(imei)) return "00 00 00 00 00 00 00 00";
            if (imei.Length % 2 != 0) imei = "0" + imei;
            return string.Join(" ", Enumerable.Range(0, imei.Length / 2).Select(i => imei.Substring(i * 2, 2)));
        }

        private string TextToHexSpaces(string text)
        {
            return BitConverter.ToString(Encoding.ASCII.GetBytes(text)).Replace("-", " ");
        }
    }
}