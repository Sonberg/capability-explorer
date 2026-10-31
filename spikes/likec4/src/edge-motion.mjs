// Decorative particles overlay the paths without changing certainty line styles.
// SVG motion is created only while enabled; cleanup removes every animation.
export function animateEdges(container) {
  const particles = new Map();
  const ns = 'http://www.w3.org/2000/svg';
  function pathsIn(root) {
    return [...root.querySelectorAll('.react-flow__edge-path'),
      ...[...root.querySelectorAll('*')].flatMap(el => el.shadowRoot ? pathsIn(el.shadowRoot) : [])];
  }
  function sync() {
    const paths = new Set(pathsIn(container));
    for (const [path, particle] of particles) if (!paths.has(path)) {particle.remove();particles.delete(path);}
    for (const path of paths) {
      let particle=particles.get(path);
      if (!particle || !particle.isConnected) {
        particle=document.createElementNS(ns,'circle');
        particle.setAttribute('r','3.5');particle.setAttribute('fill','#7dd3fc');
        particle.setAttribute('class','ce-motion-particle');particle.style.pointerEvents='none';
        const motion=document.createElementNS(ns,'animateMotion');
        motion.setAttribute('dur','4s');motion.setAttribute('repeatCount','indefinite');
        particle.append(motion);path.parentNode.append(particle);particles.set(path,particle);
      }
      const motion=particle.firstChild, d=path.getAttribute('d') ?? '';
      if (motion.getAttribute('path') !== d) motion.setAttribute('path',d);
    }
  }
  sync();
  const timer=setInterval(sync,200);
  return () => {clearInterval(timer);for (const particle of particles.values())particle.remove();};
}
