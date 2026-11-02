/** Refuse unqualified paths before legacy Docker execution. No receipt changes.
 * @param {string} feature @returns {void} */
export function requireDockerRuntime(feature) {
  const engine = process.env.CE_CONTAINER_ENGINE ?? 'docker';
  if (!['docker', 'podman'].includes(engine)) throw new Error('CE_CONTAINER_ENGINE must be docker or podman');
  if (engine === 'podman') throw new Error(`${feature} is not qualified for the partial Podman core profile; no Docker fallback attempted`);
}
