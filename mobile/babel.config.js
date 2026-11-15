module.exports = {
  presets: ['module:@react-native/babel-preset'],
  plugins: [function apiEnvironment({ types }) {
    return { visitor: { MemberExpression(path) {
      if (!path.matchesPattern('process.env.ASSET_DEBUG_API_URL') && !path.matchesPattern('process.env.ASSET_RELEASE_API_URL')) return;
      const key = path.node.property.name;
      const value = process.env[key] || '';
      if (value) {
        const url = new URL(value);
        if (!['http:', 'https:'].includes(url.protocol) || (key === 'ASSET_RELEASE_API_URL' && url.protocol !== 'https:')) throw new Error(`${key} must be a valid ${key.includes('RELEASE') ? 'HTTPS' : 'HTTP(S)'} URL`);
      }
      path.replaceWith(types.stringLiteral(value));
    } } };
  }],
};
