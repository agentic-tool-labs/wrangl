const target = new URL("../../web/ui/vendor/preact.js", import.meta.url).href;

export async function resolve(specifier, context, next) {
  if (specifier === "preact") {
    return { url: target, shortCircuit: true };
  }
  return next(specifier, context);
}
