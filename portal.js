(() => {
  const loader = document.getElementById('loader');
  const bar = document.getElementById('loadbar');
  const status = document.getElementById('loadstatus');
  let progress = 0;
  const timer = setInterval(() => {
    progress = Math.min(100, progress + 4 + Math.random() * 13);
    bar.style.width = `${progress}%`;
    status.textContent = progress < 30 ? 'Preparing your workspace…'
      : progress < 60 ? 'Mapping the flight arena…'
        : progress < 90 ? 'Calibrating 3D viewport…' : 'Workspace ready';
    if (progress >= 100) {
      clearInterval(timer);
      setTimeout(() => loader.classList.add('done'), 350);
    }
  }, 90);

  const mount = document.getElementById('mount');
  const fallback = document.getElementById('fallback');
  const obstacleLayout = [
    [-23, -16, 9, 9, 7], [-7, -23, 7, 13, 10], [14, -14, 12, 8, 8],
    [24, 7, 8, 11, 12], [6, 22, 10, 9, 7], [-20, 20, 7, 12, 9],
    [-2, 3, 6, 8, 11], [32, -29, 6, 7, 7]
  ];
  let scene;
  let camera;
  let renderer;
  let drone;
  let grid;
  let blocks;
  let controls;
  let frame = 0;
  let showGrid = true;
  let showBlocks = true;
  let light = false;

  function init() {
    if (!window.THREE) {
      fallback.textContent = '3D library unavailable — check your internet connection and reload.';
      return;
    }
    try {
      scene = new THREE.Scene();
      scene.background = new THREE.Color(0x080c16);
      scene.fog = new THREE.FogExp2(0x080c16, 0.012);
      camera = new THREE.PerspectiveCamera(50, mount.clientWidth / mount.clientHeight, 0.1, 1000);
      camera.position.set(36, 30, 42);
      renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
      renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
      renderer.setSize(mount.clientWidth, mount.clientHeight);
      renderer.outputColorSpace = THREE.SRGBColorSpace;
      renderer.toneMapping = THREE.ACESFilmicToneMapping;
      renderer.toneMappingExposure = 1.1;
      mount.appendChild(renderer.domElement);
      fallback.style.display = 'none';

      scene.add(new THREE.HemisphereLight(0x9bbcff, 0x11182b, 1.7));
      const sun = new THREE.DirectionalLight(0x7ba8ff, 2.3);
      sun.position.set(18, 35, 15);
      scene.add(sun);
      const glow = new THREE.PointLight(0x1b9dff, 45, 85);
      glow.position.set(-15, 10, -12);
      scene.add(glow);

      const starPositions = [];
      for (let i = 0; i < 1000; i++)
        starPositions.push((Math.random() - 0.5) * 220, Math.random() * 100 + 8, (Math.random() - 0.5) * 220);
      const stars = new THREE.BufferGeometry();
      stars.setAttribute('position', new THREE.Float32BufferAttribute(starPositions, 3));
      scene.add(new THREE.Points(stars, new THREE.PointsMaterial({ color: 0x91b5ff, size: 0.18, transparent: true, opacity: 0.7 })));

      const platform = new THREE.Mesh(
        new THREE.BoxGeometry(100, 1.2, 100),
        new THREE.MeshStandardMaterial({ color: 0x111b2d, metalness: 0.6, roughness: 0.5 })
      );
      platform.position.y = -1.1;
      scene.add(platform);
      const edges = new THREE.LineSegments(
        new THREE.EdgesGeometry(platform.geometry),
        new THREE.LineBasicMaterial({ color: 0x2f6fe8 })
      );
      edges.position.copy(platform.position);
      scene.add(edges);
      grid = new THREE.GridHelper(100, 50, 0x3167b9, 0x1a2d4c);
      grid.position.y = -0.47;
      scene.add(grid);

      for (let radius = 9; radius <= 27; radius += 9) {
        const ring = new THREE.Mesh(
          new THREE.TorusGeometry(radius, 0.025, 4, 120),
          new THREE.MeshBasicMaterial({ color: 0x28518a, transparent: true, opacity: 0.7 })
        );
        ring.rotation.x = Math.PI / 2;
        ring.position.y = -0.42;
        scene.add(ring);
      }

      blocks = new THREE.Group();
      scene.add(blocks);
      obstacleLayout.forEach(([x, z, width, depth, height], i) => {
        const mesh = new THREE.Mesh(
          new THREE.BoxGeometry(width, height, depth),
          new THREE.MeshStandardMaterial({
            color: i % 2 ? 0x1b2a43 : 0x243653,
            metalness: 0.45,
            roughness: 0.4,
            emissive: i % 3 === 0 ? 0x071d42 : 0x020711
          })
        );
        mesh.position.set(x, height / 2 - 0.45, z);
        blocks.add(mesh);
        const edge = new THREE.LineSegments(
          new THREE.EdgesGeometry(mesh.geometry),
          new THREE.LineBasicMaterial({ color: i % 2 ? 0x3d78c7 : 0x2a547f })
        );
        edge.position.copy(mesh.position);
        blocks.add(edge);
      });
      document.getElementById('obstacleCount').textContent = String(obstacleLayout.length).padStart(2, '0');

      const pad = new THREE.Mesh(
        new THREE.CylinderGeometry(5, 5, 0.16, 64),
        new THREE.MeshStandardMaterial({ color: 0x103d4b, emissive: 0x063f53 })
      );
      pad.position.set(34, -0.25, 31);
      scene.add(pad);
      const targetRing = new THREE.Mesh(
        new THREE.TorusGeometry(5.4, 0.12, 8, 64),
        new THREE.MeshBasicMaterial({ color: 0x50d9e8 })
      );
      targetRing.rotation.x = Math.PI / 2;
      targetRing.position.set(34, -0.13, 31);
      scene.add(targetRing);

      drone = new THREE.Group();
      drone.add(new THREE.Mesh(
        new THREE.BoxGeometry(2.1, 0.65, 2.1),
        new THREE.MeshStandardMaterial({ color: 0x9dbbff, metalness: 0.7, roughness: 0.25, emissive: 0x102b68 })
      ));
      const core = new THREE.Mesh(
        new THREE.SphereGeometry(0.55, 20, 20),
        new THREE.MeshStandardMaterial({ color: 0x50d9e8, emissive: 0x0b7caa, emissiveIntensity: 2 })
      );
      core.position.y = 0.4;
      drone.add(core);
      for (const [x, z] of [[-2.4, -2.4], [2.4, -2.4], [-2.4, 2.4], [2.4, 2.4]]) {
        const arm = new THREE.Mesh(
          new THREE.CylinderGeometry(0.13, 0.13, 3.4, 8),
          new THREE.MeshStandardMaterial({ color: 0xc6d7ff, metalness: 0.75 })
        );
        arm.rotation.z = Math.PI / 2;
        arm.position.set(x / 2, 0.05, z / 2);
        arm.rotation.y = Math.atan2(z, x);
        drone.add(arm);
        const rotor = new THREE.Mesh(
          new THREE.TorusGeometry(0.85, 0.055, 6, 32),
          new THREE.MeshBasicMaterial({ color: 0x5d8dff })
        );
        rotor.rotation.x = Math.PI / 2;
        rotor.position.set(x, 0.15, z);
        drone.add(rotor);
      }
      drone.position.set(-38, 8, 35);
      scene.add(drone);
      scene.add(new THREE.AxesHelper(5));

      controls = { drag: false, pan: false, x: 0, y: 0, theta: 0.72, phi: 0.82, radius: 56, target: new THREE.Vector3(0, 0, 0) };
      const canvas = renderer.domElement;
      canvas.addEventListener('pointerdown', event => {
        controls.drag = true;
        controls.pan = event.button === 2;
        controls.x = event.clientX;
        controls.y = event.clientY;
        canvas.setPointerCapture(event.pointerId);
      });
      canvas.addEventListener('pointermove', event => {
        if (!controls.drag) return;
        const dx = event.clientX - controls.x;
        const dy = event.clientY - controls.y;
        controls.x = event.clientX;
        controls.y = event.clientY;
        if (controls.pan) {
          controls.target.x -= dx * controls.radius * 0.00035;
          controls.target.z += dy * controls.radius * 0.00035;
        } else {
          controls.theta -= dx * 0.006;
          controls.phi = Math.max(0.18, Math.min(1.45, controls.phi + dy * 0.005));
        }
        cameraUpdate();
      });
      canvas.addEventListener('pointerup', () => { controls.drag = false; });
      canvas.addEventListener('contextmenu', event => event.preventDefault());
      canvas.addEventListener('wheel', event => {
        controls.radius = Math.max(22, Math.min(100, controls.radius + event.deltaY * 0.045));
        cameraUpdate();
      }, { passive: true });
      window.addEventListener('resize', () => {
        if (!renderer) return;
        camera.aspect = mount.clientWidth / Math.max(1, mount.clientHeight);
        camera.updateProjectionMatrix();
        renderer.setSize(mount.clientWidth, mount.clientHeight);
      });
      cameraUpdate();
      window.skyrlWorld = { scene, camera, renderer, controls, obstacleLayout, groundY: -0.5, drone, blocks, cameraUpdate };
      animate();
    } catch (error) {
      fallback.style.display = 'grid';
      fallback.textContent = 'Could not initialize the 3D scene in this browser.';
      console.error(error);
    }
  }

  function cameraUpdate() {
    if (!controls) return;
    const c = controls;
    camera.position.set(
      c.target.x + c.radius * Math.sin(c.phi) * Math.sin(c.theta),
      c.target.y + c.radius * Math.cos(c.phi),
      c.target.z + c.radius * Math.sin(c.phi) * Math.cos(c.theta)
    );
    camera.lookAt(c.target);
  }

  let previousFrameTime = 0;
  function animate(timestamp = 0) {
    requestAnimationFrame(animate);
    if (!renderer || !scene) return;
    const deltaTime = previousFrameTime ? Math.min((timestamp - previousFrameTime) / 1000, 0.05) : 0;
    previousFrameTime = timestamp;
    frame++;
    if (drone) {
      if (window.SkyrlSonar) window.SkyrlSonar.update(deltaTime);   // sonar navigation (sonar.js)
      else {
        drone.position.y = 8 + Math.sin(frame * 0.025) * 0.35;
        drone.rotation.y += 0.001;
      }
    }
    renderer.render(scene, camera);
  }

  document.querySelectorAll('[data-mode]').forEach(button => button.addEventListener('click', () => {
    document.querySelectorAll('[data-mode]').forEach(item => item.classList.remove('active'));
    button.classList.add('active');
    if (!controls) return;
    if (button.dataset.mode === 'orbit') {
      controls.target.set(0, 0, 0);
      controls.radius = 56;
      controls.theta = 0.72;
      controls.phi = 0.82;
      cameraUpdate();
    }
    if (button.dataset.mode === 'grid') {
      showGrid = !showGrid;
      grid.visible = showGrid;
      document.getElementById('gridbtn').classList.toggle('active', showGrid);
    }
    if (button.dataset.mode === 'obstacles') {
      showBlocks = !showBlocks;
      blocks.visible = showBlocks;
      button.classList.toggle('active', showBlocks);
    }
  }));
  document.getElementById('gridbtn').addEventListener('click', () => {
    if (!grid) return;
    showGrid = !showGrid;
    grid.visible = showGrid;
    document.getElementById('gridbtn').classList.toggle('active', showGrid);
  });
  document.getElementById('labelbtn').addEventListener('click', () => {
    const active = document.getElementById('labelbtn').classList.toggle('active');
    document.querySelector('.target').style.opacity = active ? '1' : '0';
    document.querySelector('.axis').style.opacity = active ? '1' : '0';
  });
  document.getElementById('run').addEventListener('click', () => {
    if (!drone || !window.SkyrlSonar) {
      alert('3D scene did not load. Check your connection and reload.');
      return;
    }
    window.SkyrlSonar.toggleRun();
  });
  document.getElementById('reset').addEventListener('click', () => {
    frame = 0;
    if (window.SkyrlSonar) window.SkyrlSonar.reset();
    document.getElementById('run').textContent = '▶  Run sonar flight';
    document.getElementById('agentStatus').textContent = '● IDLE';
    document.getElementById('altitude').innerHTML = '12.0 <i>m</i>';
    document.getElementById('velocity').innerHTML = '0.0 <i>m/s</i>';
    document.getElementById('heading').textContent = '090°';
    document.getElementById('coords').textContent = 'X 0.0 / Y 0.0 / Z 0.0';
    if (controls) {
      controls.target.set(0, 0, 0);
      controls.radius = 56;
      controls.theta = 0.72;
      controls.phi = 0.82;
      cameraUpdate();
    }
  });
  document.getElementById('lighting').addEventListener('click', () => {
    light = !light;
    if (scene) {
      scene.background.set(light ? 0x11121b : 0x080c16);
      scene.fog.color.set(light ? 0x11121b : 0x080c16);
    }
    document.getElementById('lighting').textContent = light ? '◐' : '☼';
  });

  init();
})();
