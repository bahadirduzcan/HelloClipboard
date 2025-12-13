using System;

namespace HelloClipboard.Utils
{
	public static class CharHelper
	{
		public static string GetLeadingChars(string text,char end)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			int lessThanIndex = text.IndexOf(end);

			// Eğer '<' karakteri hiç yoksa veya başta ise, ilk 20 karakteri alalım
			if (lessThanIndex <= 0)
			{
				int length = Math.Min(text.Length, 20);
				return text.Substring(0, length);
			}

			// '<' karakterine kadar olan kısmı al
			string leadingPart = text.Substring(0, lessThanIndex);

			// Görünmez karakterleri daha iyi görmek için Unicode gösterimlerine çevirelim
			var result = new System.Text.StringBuilder();
			foreach (char c in leadingPart)
			{
				// Eğer standart yazdırılabilir bir karakter değilse (ASCII 32-126 aralığı dışında ve boşluksa)
				if (char.IsControl(c) || char.IsWhiteSpace(c))
				{
					// Karakterin Unicode kodunu (Hex formatında) gösterelim
					result.Append($"[U+{(int)c:X4}]");
				}
				else
				{
					// Normal karakteri ekle (Çok nadir bir durum)
					result.Append(c);
				}
			}
			return result.ToString();
		}
	}
}
