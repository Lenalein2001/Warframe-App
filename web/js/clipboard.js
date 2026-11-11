// Called directly from the click handler, before fetching item details, so
// WebKit retains the user gesture required for clipboard writes.
export async function copyRelicName(name, clipboard = globalThis.navigator?.clipboard) {
  const text = typeof name === "string" ? name.trim() : "";
  if (!text || !clipboard?.writeText) return false;
  try { await clipboard.writeText(text); return true; }
  catch { return false; }
}
