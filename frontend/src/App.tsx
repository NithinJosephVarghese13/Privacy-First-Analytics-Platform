import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom';
import { KeycloakProvider } from './auth/KeycloakProvider';
import DashboardLayout from './layouts/DashboardLayout';
import DashboardPage from './pages/Dashboard/DashboardPage';

function App() {
  return (
    <KeycloakProvider>
      <Router>
        <Routes>
          <Route path="/" element={<DashboardLayout />}>
            <Route index element={<Navigate to="/dashboard" replace />} />
            <Route path="dashboard" element={<DashboardPage />} />
          </Route>
        </Routes>
      </Router>
    </KeycloakProvider>
  );
}

export default App;
